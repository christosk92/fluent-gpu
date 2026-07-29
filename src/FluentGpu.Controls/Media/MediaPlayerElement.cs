using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
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
/// The real <c>MediaPlayerElement</c> (spec §4.3) — the flagship proof control of the overhauled architecture. Binds a
/// headless <see cref="IMediaPlayer"/> to a composited video layer + pure-FluentGpu transport chrome, built ON the new
/// engine seams:
/// <list type="bullet">
/// <item><b>Pure Render.</b> Render has NO side effects. The per-frame video pump (viewport + <see cref="IMediaPlayer.PumpVideo"/>)
///   is a callback registered once at mount and invoked by the engine each frame (<see cref="VideoSurfaceRegistry.PumpAll"/>) —
///   it reads the live laid-out area, so the video tracks layout without the control re-rendering.</item>
/// <item><b>No whole-player frame-clock re-render.</b> Position drives compositor binds (the seek fill/playhead in
///   <see cref="MediaSeekBar"/>) and a per-second quantized time label (<see cref="MediaTransportTime"/>); the player
///   component re-renders only on LOW-frequency signal changes, never per frame.</item>
/// <item><b>First-class fullscreen hand-off.</b> The fullscreen presentation shares the inline surface via an explicit
///   single-writer ownership transfer on the registry (<see cref="VideoSurfaceRegistry.TransferOwnership"/>) — a
///   non-owner pump is a no-op, so the two views never fight over the shared slot.</item>
/// <item><b>Declarative idle-hide.</b> Auto-hide is a <see cref="Component.UseTimeout"/> restarted on pointer activity
///   that consults <see cref="IOverlayService.IsAnchorPinned"/> — an open picker pins the chrome, no hand-rolled counter.</item>
/// <item><b>Controlled inputs + tokens.</b> Aspect/fullscreen are concrete signals (auto-materialized when absent); all
///   on-media ink/scrim/stage reads a <c>Tok.*</c> media token — no hardcoded colors.</item>
/// </list>
/// The default transport is pure FluentGpu (our own GPU text + a scrub <c>Slider</c>) — there is NO OS control to crash
/// on. TerraFX-free: references only Engine/Controls types + the <see cref="IMediaPlayer"/>/<see cref="VideoBinding"/> seam.
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
    /// <summary>Round the composited video's corners (DIP; 0 = square, the default). The frame composites in its own
    /// DirectComposition visual OUTSIDE the UI back buffer, so a rounded parent with ClipToBounds cannot clip it — the
    /// radius has to reach the compositor, which is what this does. Half the shorter side gives a circle.</summary>
    public float CornerRadius { get; init; }
    /// <summary>Decoration mode: a BARE composited surface that owns none of the player frame. No minimum height, no
    /// border, no poster and no status overlay; the UI-side corners follow <see cref="CornerRadius"/> instead of the
    /// standard overlay radius, and everything before first frame paints transparent so whatever the caller stacked
    /// underneath (a still, a placeholder) shows through.
    ///
    /// Use for a clip that IS part of a layout's own shape — an artist portrait, a hover preview — never for a player
    /// the user operates. The default 160 DIP minimum height exists so a real player cannot collapse to a sliver; in a
    /// small decorative slot that same floor turns the composited rect into a capsule that overflows its container.</summary>
    public bool IsDecorative { get; init; }

    /// <summary>The UI-side corner shape of the frame and the video stage. Fullscreen is square; decoration follows
    /// <see cref="CornerRadius"/> so the UI clip agrees with the compositor clip (a decorative surface's shape IS the
    /// caller's shape); everything else keeps the standard overlay radius.</summary>
    private CornerRadius4 FrameCorners =>
        IsFullscreenPresentation ? default
        : IsDecorative ? CornerRadius4.All(CornerRadius)
        : Radii.OverlayAll;
    /// <summary>Show the transport controls. Never crashes across OS versions (there is no OS control). Default true.</summary>
    public bool AreTransportControlsEnabled { get; init; } = true;
    /// <summary>Hide overlay chrome after inactivity while playback advances. Pointer/focus/touch reveal it.</summary>
    public bool AutoHideTransportControls { get; init; } = true;
    /// <summary>Idle time before playing chrome fades away. Defaults to 2.5 seconds.</summary>
    public float TransportControlsHideDelayMs { get; init; } = 2500f;
    /// <summary>How the frame scales into the video area. Default <see cref="MediaStretch.Uniform"/>.</summary>
    public MediaStretch Stretch { get; init; } = MediaStretch.Uniform;
    /// <summary>Optional controlled aspect policy (the G5b controlled-input contract). When present it overrides
    /// <see cref="Stretch"/>; the built-in aspect menu writes it. Absent → the control materializes its own signal.</summary>
    public Signal<VideoAspectMode>? AspectMode { get; init; }
    /// <summary>Controlled display aspect for <see cref="VideoAspectMode.Custom"/> (for example 2.39). Auto-materialized
    /// (defaults to 16:9) when absent.</summary>
    public Signal<double>? CustomAspectRatio { get; init; }
    /// <summary>Fired after the built-in aspect menu writes the aspect signal(s) (notification sugar).</summary>
    public Action<VideoAspectMode, double>? AspectModeChanged { get; init; }
    /// <summary>The opaque stage color painted around Uniform/Native content (the letterbox/pillarbox). Defaults to
    /// video black (<see cref="Tok.MediaLetterbox"/>). It is a single fill under the whole video area, not four bars —
    /// see the painter-order note in <see cref="Render"/>.</summary>
    public ColorF LetterboxColor { get; init; } = Tok.MediaLetterbox;
    /// <summary>Paint the opaque <see cref="LetterboxColor"/> stage fill behind the video. False ⇒ the area around the
    /// fitted video stays whatever is behind the player (no letterbox).</summary>
    public bool ShowLetterboxBars { get; init; } = true;
    /// <summary>Shown over the video area until the first frame / when audio-only. Null → a default poster.</summary>
    public Element? PosterContent { get; init; }
    /// <summary>Bring-your-own transport (still reads the same bound player). Null → the default FluentGpu transport.</summary>
    public Element? TransportOverride { get; init; }

    /// <summary>Controlled fullscreen state (auto-materialized when absent).</summary>
    internal Signal<bool>? FullscreenState { get; init; }
    /// <summary>True when this instance is the fullscreen presentation (mounted in the fullscreen overlay).</summary>
    internal bool IsFullscreenPresentation { get; init; }
    /// <summary>Invoked by the fullscreen presentation to ask its host to leave fullscreen.</summary>
    internal Action? ExitFullscreen { get; init; }
    /// <summary>The inline element's surface binding, handed to the fullscreen presentation so BOTH views drive the
    /// SAME composited slot (a second <c>UseVideoSurface</c> slot would bind the same swapchain to a second visual and
    /// clobber it on exit). Which view actually pumps is decided by explicit single-writer ownership transfer on the
    /// registry — see <see cref="VideoSurfaceRegistry.TransferOwnership"/> — not by a "who am I" convention.</summary>
    internal VideoBinding PresentationBinding { get; init; }

    // Pump state (set each Render, read by the engine-invoked PumpNow — the video pump lives OUTSIDE Render).
    private VideoBinding _binding;
    private Ref<NodeHandle>? _areaRef;
    // The video-hole node. Its laid-out rect IS the video rect (the presenter is placed from it) — one source of truth.
    private Ref<NodeHandle>? _holeRef;
    private Signal<VideoAspectMode>? _aspectForPump;
    private Signal<double>? _customAspectForPump;
    private SceneStore? _scene;
    // This component's KeepAlive/window-visibility signal, read by the pump (which runs outside Render).
    private IReadSignal<bool>? _isActive;

    public override Element Render()
    {
        // IsFullscreenPresentation freezes at mount, so this conditional hook is stable for the instance's lifetime.
        // The DEFECT the rebuild fixes was never the conditional hook (call-site keying makes it legal) — it was the
        // "exactly one instance pumps" convention; that is replaced by explicit registry ownership transfer below.
        var binding = IsFullscreenPresentation ? PresentationBinding : UseVideoSurface();
        var hooks = UseContext(InputHooks.Current);
        var overlayService = UseContext(Overlay.Service);
        var areaRef = UseRef<NodeHandle>(default);
        var holeRef = UseRef<NodeHandle>(default);
        var playerRoot = UseRef<NodeHandle>(default);
        var moreAnchor = UseRef<NodeHandle>(default);
        var ccAnchor = UseRef<NodeHandle>(default);
        var qualityAnchor = UseRef<NodeHandle>(default);
        var rateAnchor = UseRef<NodeHandle>(default);
        var fullscreenHandle = UseRef<OverlayHandle?>(null);
        var localFullscreen = UseSignal(false);
        var fullscreen = FullscreenState ?? localFullscreen;
        var localAspect = UseSignal(ToAspectMode(Stretch));
        var localCustomAspect = UseSignal(16.0 / 9.0);
        var aspectSig = AspectMode ?? localAspect;                 // materialized controlled signals (no write-sniffing)
        var customAspectSig = CustomAspectRatio ?? localCustomAspect;
        var chromeVisible = UseSignal(true);
        var areaBounds = UseSignal<RectF>(default);                // video-area bounds (bounds-changed column → letterbox recompute)

        var natural = Player.NaturalSize.Value;
        var state = Player.State.Value;
        bool playIntent = Player.IsPlayRequested.Value;
        var bufferingInfo = Player.Buffering.Value;
        TimedCue? activeCue = Player.ActiveCue.Value;
        VideoAspectMode aspect = aspectSig.Value;
        double customAspect = customAspectSig.Value;
        bool audioOnly = IsAudioOnly(natural);
        bool videoReady = !audioOnly && state is not (PlaybackState.Idle or PlaybackState.Opening);
        int pinEpoch = overlayService.PinEpoch.Value;              // subscribe: re-render when a picker opens/closes
        bool pinned = !playerRoot.Value.IsNull && overlayService.IsAnchorPinned(playerRoot.Value);
        _ = pinEpoch;

        // ── the video pump lives OUTSIDE Render (fix: pure Render). Publish the inputs it reads, register it once. ──
        _binding = binding;
        _areaRef = areaRef;
        _holeRef = holeRef;
        _aspectForPump = aspectSig;
        _customAspectForPump = customAspectSig;
        _scene = Context.Scene;
        // Peeked (never .Value) by the pump, so publishing it here does not make the pump a render dependency.
        _isActive = UseIsActive();

        UseEffect(() =>
        {
            if (!binding.IsValid) return (Action?)null;
            int reg = binding.RegisterPump(this, PumpNow);   // engine invokes PumpNow each frame with the current scale
            return () => binding.UnregisterPump(reg);
        }, DepKey.Empty);

        // Single-writer ownership: the fullscreen presentation CLAIMS the shared slot on mount; the inline element
        // (re)claims it whenever NOT fullscreen (mount + every exit, incl. the overlay's closing frames). A non-owner
        // pump is a no-op — the two views never fight over the slot.
        UseEffect(() =>
        {
            if (!binding.IsValid) return;
            if (IsFullscreenPresentation) binding.TransferOwnershipTo(this);
            else if (!fullscreen.Value) binding.TransferOwnershipTo(this);   // auto-tracked on fullscreen
        });

        RectF area = areaBounds.Value;
        RectF videoRect = (audioOnly || area.W <= 0f) ? area : FitVideoRect(area, natural, aspect, customAspect);

        void RevealChrome()
        {
            if (!AreTransportControlsEnabled) return;
            chromeVisible.Value = true;   // value-gated: no re-render if already visible
        }

        void SetAspect(VideoAspectMode mode, double ratio = 0)
        {
            aspectSig.Value = mode;                                // controlled: write the signal directly
            if (ratio > 0) customAspectSig.Value = ratio;
            AspectModeChanged?.Invoke(mode, ratio > 0 ? ratio : customAspectSig.Peek());
            RevealChrome();
        }

        void LeaveFullscreen()
        {
            if (!fullscreen.Peek() && fullscreenHandle.Value is null) return;
            fullscreen.Value = false;                              // inline ownership effect reclaims on this edge
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
                    AspectMode = aspectSig,
                    CustomAspectRatio = customAspectSig,
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
        bool showChrome = AreTransportControlsEnabled && (forceChrome || pinned || chromeVisible.Value);

        // ── declarative idle-hide (replaces the ToolTipClock borrow + the overlayPin counter): a one-shot timer that
        // hides the chrome after inactivity, RESTARTED on pointer activity, armed only while actively playing and not
        // pinned/force-shown. An open picker pins the anchor (PinEpoch above) ⇒ armed=false ⇒ timer cancelled ⇒ chrome
        // stays; closing the picker re-arms it.
        bool autoHideArmed = AreTransportControlsEnabled && AutoHideTransportControls && !pinned && !forceChrome
            && playIntent && state == PlaybackState.Playing;
        var hideTimer = UseTimeout(() => chromeVisible.Value = false, MathF.Max(100f, TransportControlsHideDelayMs), DepKey.Empty);
        UseEffect(() => { if (autoHideArmed) hideTimer.Restart(); else hideTimer.Cancel(); return (Action?)null; }, autoHideArmed ? 1 : 0);
        void RevealAndRearm() { RevealChrome(); hideTimer.Restart(); }

        // A terminal failure wins over the opening spinner: otherwise a Failed state with a lingering play intent would
        // keep showing "Starting playback…" forever (the DRM-license-rejected infinite-spinner bug).
        // Decoration never reports status: a spinner or an error card inside an artist portrait is worse than the still
        // the caller already has underneath. Failures stay observable through Player.Error/State for the caller to log.
        Element? statusOverlay = IsDecorative
            ? null
            : state == PlaybackState.Failed
            ? FailedOverlay(Player.Error.Value?.Message)
            : !videoReady && (state is PlaybackState.Opening or PlaybackState.Buffering || playIntent)
                ? OpeningOverlay(playIntent)
                : bufferingInfo.IsBuffering || state is PlaybackState.Buffering or PlaybackState.Stalled
                    ? BufferingOverlay(bufferingInfo)
                    : null;

        // ── the video stage (a ZStack: children paint in author order) ───────────────────────────────────────────────
        // Painter order once the frame is live:
        //   [0] the OPAQUE LetterboxColor stage fill across the WHOLE video area. This fill IS the letterbox — there
        //       are no separate bar elements any more.
        //   [1] the VIDEO HOLE PUNCH (DrawOp.DrawVideo, gpu-renderer.md §7.3), laid out at EXACTLY the fitted video
        //       rect. It paints nothing — it ERASES everything already recorded beneath it (this stage fill, the
        //       translucent page behind an inline/PiP player, the shell) toward premultiplied zero, so the DComp video
        //       visual composited z-BELOW the premultiplied UI swapchain shows through at full strength instead of
        //       blending with what stayed in the back buffer. VideoSurfaceId carries the registry slot token
        //       (diagnostic at replay — the presenter places the visual itself via PumpNow).
        //   [2…] status / caption / transport overlays — LATER siblings, so they repaint over the video and stay
        //       visible on top of it.
        // ONE SOURCE OF TRUTH: PumpNow places the DComp visual from scene.AbsoluteRect of the HOLE node, so the erased
        // region and the presented video are the same rect BY CONSTRUCTION. The old shape (a hole across the WHOLE
        // area, then letterbox bars painted around an independently recomputed fit, with the presenter placed at a
        // THIRD, pump-time fit) left sub-pixel slivers at fractional device scale: pixels the hole erased that neither
        // the video visual nor a bar covered, revealing the DWM Mica backdrop as a grey edge. Any residual sub-pixel AA
        // edge now falls on the OPAQUE stage fill — worst case a faint black hairline, never grey.
        // Not yet ready: while a loading/buffering overlay is up, show a plain dark stage — the poster's play glyph
        // underneath the spinner reads as two competing affordances.
        var videoChildren = new System.Collections.Generic.List<Element>(8);
        if (videoReady)
        {
            if (ShowLetterboxBars)
                videoChildren.Add(new BoxEl { Grow = 1f, Fill = LetterboxColor, HitTestVisible = false });
            videoChildren.Add(new BoxEl
            {
                Grow = 1f,
                AlignSelf = FlexAlign.Start,
                Margin = LetterboxInsets(area, videoRect),   // area MINUS these insets == the fitted video rect
                VideoHole = true,
                VideoSurfaceId = binding.Token,
                OnRealized = h => holeRef.Value = h,
            });
        }
        else if (IsDecorative)
            // Transparent until the first frame lands, so the caller's own still stays visible underneath rather than
            // being covered by a dark stage or a default poster.
            videoChildren.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
        else
            videoChildren.Add(statusOverlay is not null
                ? new BoxEl { Grow = 1f, Fill = Tok.MediaStage }
                : PosterContent ?? DefaultPoster());

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
            MinHeight = IsFullscreenPresentation || IsDecorative ? 0f : 160f,
            ClipToBounds = true,
            Corners = FrameCorners,
            Fill = ColorF.Transparent,
            OnRealized = h => areaRef.Value = h,
            OnBoundsChanged = b => { if (b != areaBounds.Peek()) areaBounds.Value = b; },   // resize → recompute letterbox
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
                    moreAnchor, ccAnchor, qualityAnchor, rateAnchor, overlayService)],
            });

        void HandleKey(KeyEventArgs e)
        {
            RevealAndRearm();
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

        return new BoxEl
        {
            ZStack = true,
            Grow = 1f,
            Corners = FrameCorners,
            ClipToBounds = true,
            BorderColor = IsFullscreenPresentation || IsDecorative ? ColorF.Transparent : Tok.StrokeFlyoutDefault,
            BorderWidth = IsFullscreenPresentation || IsDecorative ? 0f : 1f,
            Focusable = true,
            OnRealized = h => playerRoot.Value = h,
            OnKeyDown = HandleKey,
            OnPointerMoveWithin = _ => RevealAndRearm(),
            OnPointerPressed = _ => RevealAndRearm(),
            OnFocusChanged = focused => { if (focused) RevealAndRearm(); },
            Children = layers.ToArray(),
        };
    }

    /// <summary>The engine-invoked per-frame video pump (registered at mount; see <see cref="VideoPump"/>). Reads the
    /// live laid-out video-area rect + the current scale and drives <see cref="IMediaPlayer.PumpVideo"/> — NO side
    /// effect ever runs in Render. Zero-alloc (all struct math + value-gated intents). A no-op when a non-owner (the
    /// registry only invokes the current owner's pump).
    /// <para>The video rect is read from the HOLE node itself — the same node whose rect the recorder erases — so the
    /// erased region and the composited video visual are identical by construction (no second, independently computed
    /// fit to drift out of alignment at fractional device scale). The viewport stays the whole area: it is what clips a
    /// <see cref="VideoAspectMode.UniformToFill"/> crop, whose fitted rect deliberately overflows the stage.</para></summary>
    private void PumpNow(float scale)
    {
        VideoBinding b = _binding;
        if (!b.IsValid) return;
        // Parked (Flow.KeepAlive) or minimized: hide the composited surface. The video visual lives in its OWN
        // DirectComposition visual below the UI swapchain, so it is not clipped or covered by whatever the shell draws
        // next — a parked page's frame keeps compositing at its last placement (a navigated-away artist portrait on the
        // nav rail). Decorative clips SKIP the pump while inactive (they must not keep an MF session alive off-screen).
        // Non-decorative player surfaces (PiP / pop-out) still pump: the MF session only advances while pumped, and
        // NaturalSize / duration never publish without it — hiding without pumping is the black Loading poster over audio.
        if (_isActive is { } act && !act.Peek())
        {
            b.SetVisible(false);
            if (IsDecorative) return;
        }
        float s = scale <= 0f ? 1f : scale;
        var scene = _scene;
        NodeHandle h = _areaRef?.Value ?? default;
        // Non-decorative player surfaces must keep calling PumpVideo even before the area is laid out (remount /
        // generation swap frames): MF only publishes duration + NaturalSize inside PumpVideo. Returning early here is
        // what left a video→video successor stuck on the Opening/Loading poster at 0:00 with no duration adopt.
        if (scene is null || h.IsNull || !scene.IsLive(h))
        {
            if (!IsDecorative) Player.PumpVideo(b, default, s);
            return;
        }
        RectF area = scene.AbsoluteRect(h);
        if (area.W <= 0f || area.H <= 0f)
        {
            if (!IsDecorative) Player.PumpVideo(b, default, s);
            return;
        }
        SizeI natural = Player.NaturalSize.Peek();
        bool audioOnly = IsAudioOnly(natural);
        RectF videoRect = area;
        if (!audioOnly)
        {
            // Fallback while the hole is not (yet) realized — the first frame after the stream goes ready, or any frame
            // with no hole at all: recompute the fit so the pump never goes dark.
            NodeHandle hole = _holeRef?.Value ?? default;
            bool live = !hole.IsNull && scene.IsLive(hole);
            videoRect = live ? scene.AbsoluteRect(hole) : default;
            if (!live || videoRect.W <= 0f || videoRect.H <= 0f)
                videoRect = FitVideoRect(area, natural, _aspectForPump?.Peek() ?? VideoAspectMode.Uniform,
                    _customAspectForPump?.Peek() ?? (16.0 / 9.0));
        }
        // Clip the viewport to every clipping ancestor before handing it to the presenter. The frame composites in its
        // OWN DirectComposition visual BELOW the UI swapchain — it is not inside any scissor the UI draws with — so a
        // viewport of the element's own rect keeps presenting at full size while the element scrolls out of its
        // scroller. That is how a video in a list ended up painted across the tab strip and the title bar.
        RectF viewport = ClipToAncestors(scene, h, area);
        if (viewport.W <= 0.5f || viewport.H <= 0.5f) { b.SetVisible(false); return; }
        b.SetViewport(viewport);
        // Clamped so a caller asking for "fully round" on a non-square element still gets a clean capsule rather than
        // radii that overlap and degenerate. CornerRadius freezes at mount, so this is a constant per element.
        if (CornerRadius > 0f)
            b.SetCornerRadius(MathF.Min(CornerRadius, MathF.Min(videoRect.W, videoRect.H) * 0.5f));
        Player.PumpVideo(b, videoRect, s);
        if (audioOnly) b.SetVisible(false);
    }

    /// <summary>Intersect <paramref name="rect"/> with the bounds of every <c>ClipsToBounds</c> ancestor.
    ///
    /// The UI gets this for free from the scissor stack; a composited video visual does not, because it lives outside
    /// the UI back buffer entirely. Walking the parent chain is cheap — it runs once per pumped surface per frame, and
    /// the chain is a handful of nodes deep.</summary>
    private static RectF ClipToAncestors(SceneStore scene, NodeHandle node, RectF rect)
    {
        for (NodeHandle p = scene.Parent(node); !p.IsNull && scene.IsLive(p); p = scene.Parent(p))
        {
            if ((scene.Flags(p) & NodeFlags.ClipsToBounds) == 0) continue;
            RectF c = scene.AbsoluteRect(p);
            float x0 = MathF.Max(rect.X, c.X), y0 = MathF.Max(rect.Y, c.Y);
            float x1 = MathF.Min(rect.X + rect.W, c.X + c.W), y1 = MathF.Min(rect.Y + rect.H, c.Y + c.H);
            rect = new RectF(x0, y0, MathF.Max(0f, x1 - x0), MathF.Max(0f, y1 - y0));
            if (rect.W <= 0f || rect.H <= 0f) return rect;
        }
        return rect;
    }

    // ── default transport (pure FluentGpu: play/pause, scrub Slider, GPU time text, mute) ────────────────────────────

    private Element BuildTransport(float areaWidth, VideoAspectMode aspect, double customAspect,
        Action<VideoAspectMode, double> setAspect, Action toggleFullscreen, Ref<NodeHandle> moreAnchor,
        Ref<NodeHandle> ccAnchor, Ref<NodeHandle> qualityAnchor, Ref<NodeHandle> rateAnchor,
        IOverlayService overlayService)
    {
        // The transport render reads only LOW-frequency signals (play-state + muted) so it does NOT re-render each frame
        // as the playhead advances. The seek scrub bar is an AUTONOMOUS component (its own scrub gate + compositor-bound
        // playhead — see MediaSeekBar), and the time label is an isolated leaf that re-renders on its own ~1 Hz tick.
        bool playIntent = Player.IsPlayRequested.Value;        // intent wins during Opening/Buffering (early Play)
        bool muted = Player.Muted.Value;                       // subscribe (low-frequency)
        MediaCommandFlags commands = Player.Commands.Available.Value;
        TimelineInfo timeline = Player.Timeline.Value;
        _ = Player.Tracks.Audio.Version.Value;
        _ = Player.Tracks.Text.Version.Value;
        _ = Player.Qualities.Variants.Version.Value;
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
            controls.Add(TextButton(timeline.IsAtLiveEdge ? MediaStrings.LiveEdge : MediaStrings.GoLive, () => _ = Player.GoLiveAsync(), timeline.IsAtLiveEdge));
        // Each inline button opens a PICKER flyout at its own anchor (never blind-cycles through the options).
        if (!compact && (commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
            controls.Add(TextButton(text is null ? MediaStrings.CaptionsShort : MediaStrings.CaptionsFor(text.Language ?? text.Label),
                () => OpenPicker(ccAnchor, CaptionItems()), text is not null) with { OnRealized = h => ccAnchor.Value = h });
        if (!compact && (commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
            controls.Add(TextButton(QualityLabel(quality), () => OpenPicker(qualityAnchor, QualityItems()))
                with { OnRealized = h => qualityAnchor.Value = h });
        if (!compact && (commands & MediaCommandFlags.Rate) != 0)
            controls.Add(TextButton(MediaStrings.RateLabel(rate), () => OpenPicker(rateAnchor, SpeedItems()))
                with { OnRealized = h => rateAnchor.Value = h });
        controls.Add(IconButton(Icons.More, OpenMore) with { OnRealized = h => moreAnchor.Value = h });
        controls.Add(IconButton(IsFullscreenPresentation ? Icons.BackToWindow : Icons.FullScreen, toggleFullscreen));

        return new BoxEl
        {
            Direction = 1,
            Gap = 2f,
            Padding = new Edges4(14, 34, 14, 8),
            // The canonical media footer scrim (Tok.ScrimBottom): controls sit on darkness that dissolves into the
            // video, the YouTube/Netflix-style overlay read.
            Gradient = Tok.ScrimBottom,
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
            // PopupOptions.PinsAnchor defaults true → the open flyout PINS the player's auto-hide scope; the chrome's
            // idle-hide consults IsAnchorPinned/PinEpoch and stays put. No hand-rolled counter, no ClosedAction unpin.
        }

        System.Collections.Generic.List<MenuFlyoutItem> CaptionItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Text.Count + 1)
            { MenuFlyoutItem.RadioItem(MediaStrings.Off, text is null, () => _ = Player.SelectTrackAsync(null)) };
            for (int i = 0; i < Player.Tracks.Text.Count; i++)
            {
                MediaTrack track = Player.Tracks.Text[i];
                items.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? MediaStrings.CaptionsIndexed(i + 1), text?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> QualityItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Qualities.Variants.Count + 1)
            { MenuFlyoutItem.RadioItem(MediaStrings.Auto, quality.IsAuto, () => _ = Player.SelectQualityAsync(QualitySelection.Auto)) };
            for (int i = 0; i < Player.Qualities.Variants.Count; i++)
            {
                QualityVariant variant = Player.Qualities.Variants[i];
                string id = variant.Id;
                string label = variant.Resolution.Height > 0 ? MediaStrings.QualityHeight(variant.Resolution.Height) : variant.Label ?? id;
                items.Add(MenuFlyoutItem.RadioItem(label, !quality.IsAuto && quality.VariantId == id,
                    () => _ = Player.SelectQualityAsync(QualitySelection.Pin(id))));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> AudioItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Audio.Count);
            MediaTrack? audio = Player.Tracks.SelectedAudio.Peek();
            for (int i = 0; i < Player.Tracks.Audio.Count; i++)
            {
                MediaTrack track = Player.Tracks.Audio[i];
                items.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? MediaStrings.AudioIndexed(i + 1), audio?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> SpeedItems() =>
        [
            Speed(0.5), Speed(0.75), Speed(1), Speed(1.25), Speed(1.5), Speed(2),
        ];

        MenuFlyoutItem Speed(double value) => MenuFlyoutItem.RadioItem(MediaStrings.RateLabel((float)value), Math.Abs(rate - value) < 0.01,
            () => Player.SetRate(value));

        void OpenMore()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(12);
            items.Add(MenuFlyoutItem.SubMenu(MediaStrings.AspectRatio,
            [
                MenuFlyoutItem.RadioItem(MediaStrings.AspectFit, aspect == VideoAspectMode.Uniform, () => setAspect(VideoAspectMode.Uniform, 0)),
                MenuFlyoutItem.RadioItem(MediaStrings.AspectCrop, aspect == VideoAspectMode.UniformToFill, () => setAspect(VideoAspectMode.UniformToFill, 0)),
                MenuFlyoutItem.RadioItem(MediaStrings.AspectStretch, aspect == VideoAspectMode.Fill, () => setAspect(VideoAspectMode.Fill, 0)),
                MenuFlyoutItem.RadioItem(MediaStrings.AspectNative, aspect == VideoAspectMode.Native, () => setAspect(VideoAspectMode.Native, 0)),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.RadioItem(MediaStrings.Ratio169, aspect == VideoAspectMode.Custom && Near(customAspect, 16.0 / 9.0), () => setAspect(VideoAspectMode.Custom, 16.0 / 9.0)),
                MenuFlyoutItem.RadioItem(MediaStrings.Ratio43, aspect == VideoAspectMode.Custom && Near(customAspect, 4.0 / 3.0), () => setAspect(VideoAspectMode.Custom, 4.0 / 3.0)),
                MenuFlyoutItem.RadioItem(MediaStrings.Ratio219, aspect == VideoAspectMode.Custom && Near(customAspect, 21.0 / 9.0), () => setAspect(VideoAspectMode.Custom, 21.0 / 9.0)),
                MenuFlyoutItem.RadioItem(MediaStrings.Ratio239, aspect == VideoAspectMode.Custom && Near(customAspect, 2.39), () => setAspect(VideoAspectMode.Custom, 2.39)),
            ], Icons.Movie));
            if ((commands & MediaCommandFlags.Rate) != 0)
                items.Add(MenuFlyoutItem.SubMenu(MediaStrings.PlaybackSpeed, SpeedItems()));
            if ((commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
                items.Add(MenuFlyoutItem.SubMenu(MediaStrings.Quality, QualityItems()));
            if ((commands & MediaCommandFlags.SelectAudioTrack) != 0 && Player.Tracks.Audio.Count > 1)
                items.Add(MenuFlyoutItem.SubMenu(MediaStrings.AudioTrack, AudioItems()));
            if ((commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
                items.Add(MenuFlyoutItem.SubMenu(MediaStrings.Captions, CaptionItems()));
            if ((commands & MediaCommandFlags.Chapters) != 0)
            {
                items.Add(MenuFlyoutItem.Separator);
                items.Add(new MenuFlyoutItem(MediaStrings.PreviousChapter, Icons.Previous, Invoke: () => _ = Player.PreviousChapterAsync()));
                items.Add(new MenuFlyoutItem(MediaStrings.NextChapter, Icons.Next, Invoke: () => _ = Player.NextChapterAsync()));
            }
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new MenuFlyoutItem(IsFullscreenPresentation ? MediaStrings.ExitFullscreen : MediaStrings.Fullscreen,
                IsFullscreenPresentation ? Icons.BackToWindow : Icons.FullScreen, Invoke: toggleFullscreen) { AcceleratorText = MediaStrings.F11 });

            OpenPicker(moreAnchor, items);
        }
    }

    /// <summary>The <c>elapsed / total</c> time label as its OWN component. It bridges the ~per-frame position/duration
    /// signals into WHOLE-SECOND value-gated signals via eager effects, so it re-renders at most ~once per second (never
    /// per frame) — the position tick reaches the compositor-bound seek bar, not this text.</summary>
    private sealed class MediaTransportTime : Component
    {
        public required IMediaPlayer Player { get; init; }
        private readonly Signal<int> _posSec = new(0);
        private readonly Signal<int> _durSec = new(-1);

        public override Element Render()
        {
            UseSignalEffect(() => { int s = (int)Player.PositionSeconds.Value; if (s != _posSec.Peek()) _posSec.Value = s; });
            UseSignalEffect(() =>
            {
                var d = Player.Duration.Value;
                int s = d > TimeSpan.Zero ? (int)d.TotalSeconds : -1;
                if (s != _durSec.Peek()) _durSec.Value = s;
            });
            int pos = _posSec.Value;
            int dur = _durSec.Value;
            return new TextEl($"{FormatTime(TimeSpan.FromSeconds(Math.Max(0, pos)))} / {FormatTime(TimeSpan.FromSeconds(Math.Max(0, dur)))}")
            {
                Size = 12f, Color = Tok.OnMediaSecondary,
            };
        }
    }

    private sealed class FullscreenMediaView : Component
    {
        public required IMediaPlayer Player { get; init; }
        public required VideoBinding Binding { get; init; }
        public required Signal<VideoAspectMode> AspectMode { get; init; }
        public required Signal<double> CustomAspectRatio { get; init; }
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
                        PresentationBinding = Binding,
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
        HoverFill = Tok.OnMediaPrimary with { A = 0.09f }, PressedFill = Tok.OnMediaPrimary with { A = 0.16f },
        Role = AutomationRole.Button,
        OnClick = onClick,
        Children = new Element[] { new TextEl(glyph) { Size = 16f, Color = Tok.OnMediaPrimary, FontFamily = Theme.IconFont } },
    };

    private static BoxEl TextButton(string label, Action onClick, bool active = false) => new()
    {
        Height = 34f, MinWidth = 42f, Padding = new Edges4(8f, 0f, 8f, 0f), Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = active ? Tok.AccentDefault : ColorF.Transparent,
        HoverFill = active ? Tok.AccentSecondary : Tok.OnMediaPrimary with { A = 0.09f },
        PressedFill = active ? Tok.AccentTertiary : Tok.OnMediaPrimary with { A = 0.16f },
        Role = AutomationRole.Button, OnClick = onClick,
        Children = [new TextEl(label) { Size = 12f, Color = Tok.OnMediaPrimary }],
    };

    private string QualityLabel(QualitySelection selection)
    {
        if (selection.IsAuto) return MediaStrings.Auto;
        for (int i = 0; i < Player.Qualities.Variants.Count; i++)
        {
            QualityVariant q = Player.Qualities.Variants[i];
            if (q.Id == selection.VariantId)
                return q.Resolution.Height > 0 ? MediaStrings.QualityHeight(q.Resolution.Height) : q.Label ?? q.Id;
        }
        return MediaStrings.Auto;
    }

    private Element DefaultPoster() => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = Tok.MediaStage,
        Children = new Element[]
        {
            new BoxEl
            {
                Width = 56f, Height = 56f, Corners = Radii.Circle(56f),
                Fill = Tok.MediaScrim,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = new Element[]
                {
                    new TextEl(Icons.Play) { Size = 24f, Color = Tok.OnMediaPrimary, FontFamily = Theme.IconFont },
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
            new TextEl(playIntent ? MediaStrings.StartingPlayback : MediaStrings.Loading)
            { Size = 13f, Color = Tok.OnMediaSecondary },
        ],
    };

    /// <summary>The per-edge letterbox insets (DIP) that place <paramref name="video"/> inside <paramref name="area"/> —
    /// the ONE piece of geometry the video stage is built from. The hole child is laid out with exactly these as its
    /// Margin, so its arranged rect IS the fitted video rect and <see cref="PumpNow"/> can place the presenter from it.
    /// Signed: a <see cref="VideoAspectMode.UniformToFill"/> crop yields NEGATIVE insets (the fitted rect overflows the
    /// stage and is clipped). Sub-half-pixel insets collapse to zero — a &lt;0.5px bar was never a bar (the same
    /// threshold <see cref="CalculateLetterboxBars"/> has always used).</summary>
    internal static Edges4 LetterboxInsets(RectF area, RectF video)
    {
        if (area.W <= 0f || area.H <= 0f) return default;
        float x = video.X - area.X, y = video.Y - area.Y;
        return new Edges4(Trim(x), Trim(y), Trim(area.W - (x + video.W)), Trim(area.H - (y + video.H)));

        static float Trim(float v) => MathF.Abs(v) < 0.5f ? 0f : v;
    }

    /// <summary>The letterbox BAR rects (area-local) for an area/video pair — the pure geometry the stage fill replaced
    /// as an element shape, kept as the unit-testable statement of the same insets.</summary>
    internal static int CalculateLetterboxBars(RectF area, RectF video, Span<RectF> bars)
    {
        if (area.W <= 0f || area.H <= 0f || bars.Length < 4) return 0;
        Edges4 inset = LetterboxInsets(area, video);
        float left = Math.Clamp(inset.Left, 0f, area.W);
        float top = Math.Clamp(inset.Top, 0f, area.H);
        float right = Math.Clamp(inset.Right, 0f, area.W);
        float bottom = Math.Clamp(inset.Bottom, 0f, area.H);
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
            BufferingReason.Seeking => MediaStrings.Seeking,
            BufferingReason.QualitySwitch => MediaStrings.ChangingQuality,
            BufferingReason.TrackSwitch => MediaStrings.ChangingTrack,
            BufferingReason.LiveCatchUp => MediaStrings.CatchingUp,
            BufferingReason.NetworkRecovery => MediaStrings.Reconnecting,
            BufferingReason.Rebuffering => MediaStrings.Buffering,
            _ => MediaStrings.Loading,
        };
        Element ring = info.Percent is >= 0 and <= 1
            ? ProgressRing.Determinate((float)info.Percent, 36f)
            : ProgressRing.Indeterminate(36f);
        return new BoxEl
        {
            Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(14f, 12f, 14f, 12f), Corners = Radii.OverlayAll,
            Fill = Tok.MediaScrim,
            Children = [ring, new TextEl(reason) { Size = 13f, Color = Tok.OnMediaPrimary }],
        };
    }

    // Terminal-failure overlay (e.g. a rejected DRM license): an error glyph + the player's typed error text (or a generic
    // fallback) INSTEAD of the opening spinner — so a failed video never reads as an eternal "Starting playback…".
    private static Element FailedOverlay(string? errorMessage) => new BoxEl
    {
        Direction = 1, Gap = 10f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(18f, 16f, 18f, 16f), Corners = Radii.OverlayAll, MaxWidth = 420f,
        Fill = Tok.MediaScrim,
        Children =
        [
            new TextEl(Icons.StatusError) { Size = 30f, Color = Tok.OnMediaPrimary, FontFamily = Theme.IconFont },
            new TextEl(string.IsNullOrWhiteSpace(errorMessage) ? MediaStrings.VideoFailed : errorMessage!)
            { Size = 13f, Color = Tok.OnMediaSecondary, Wrap = TextWrap.Wrap, MaxWidth = 380f },
        ],
    };

    private static Element CaptionOverlay(TimedCue cue) => new BoxEl
    {
        AlignSelf = FlexAlign.Center,
        MaxWidth = 880f,
        Margin = new Edges4(24f, 0f, 24f, 28f),
        Padding = new Edges4(10f, 5f, 10f, 6f),
        Corners = Radii.ControlAll,
        Fill = Tok.MediaScrim with { A = 0.72f },
        Children =
        [
            new TextEl(cue.Text)
            {
                Size = Math.Clamp(18f * cue.Style.FontScale, 12f, 40f),
                Color = cue.Style.ArgbColor == 0 ? Tok.OnMediaPrimary : FromArgb(cue.Style.ArgbColor),
                Wrap = TextWrap.Wrap,
            },
        ],
    };

    // Convert a cue-supplied 0xAARRGGBB color (dynamic subtitle data) to a ColorF via the float ctor — a runtime
    // conversion, not a hardcoded color constant, so the media element carries no baked color literals.
    private static ColorF FromArgb(uint argb)
        => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

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

/// <summary>Every user-facing string in the media player chrome, hoisted to ONE place and routed through the control-kit
/// localization pillar (G5j). Each member resolves through <c>Loc.Get</c>/<c>Loc.Format</c> against a compile-safe
/// <c>Strings.Media.*</c> key, so it renders its neutral English with zero app configuration (the kit's baked-in
/// neutral floor), an app's culture table overrides it per-key, and it re-resolves on a culture change — the chrome is
/// built inside a component <c>Render</c>, so reading these (which subscribes the culture epoch) re-renders the player
/// on a language switch. Universal notation (aspect-ratio numbers, the <c>×</c> rate symbol, the <c>p</c> resolution
/// suffix, the <c>F11</c> key name) stays invariant in C# and is deliberately NOT localized.</summary>
internal static class MediaStrings
{
    public static string StartingPlayback => Loc.Get(Strings.Media.StartingPlayback);
    public static string Loading => Loc.Get(Strings.Media.Loading);
    public static string Off => Loc.Get(Strings.Media.Off);
    public static string Auto => Loc.Get(Strings.Media.Auto);
    public static string GoLive => Loc.Get(Strings.Media.GoLive);
    public static string LiveEdge => Loc.Get(Strings.Media.LiveEdge);
    public static string CaptionsShort => Loc.Get(Strings.Media.CaptionsShort);
    public static string AspectRatio => Loc.Get(Strings.Media.AspectRatio);
    public static string AspectFit => Loc.Get(Strings.Media.AspectFit);
    public static string AspectCrop => Loc.Get(Strings.Media.AspectCrop);
    public static string AspectStretch => Loc.Get(Strings.Media.AspectStretch);
    public static string AspectNative => Loc.Get(Strings.Media.AspectNative);
    public const string Ratio169 = "16:9";                 // loc-allow: universal aspect-ratio notation, not translated
    public const string Ratio43 = "4:3";                   // loc-allow: universal aspect-ratio notation
    public const string Ratio219 = "21:9";                 // loc-allow: universal aspect-ratio notation
    public const string Ratio239 = "2.39:1";               // loc-allow: universal aspect-ratio notation
    public static string PlaybackSpeed => Loc.Get(Strings.Media.PlaybackSpeed);
    public static string Quality => Loc.Get(Strings.Media.Quality);
    public static string AudioTrack => Loc.Get(Strings.Media.AudioTrack);
    public static string Captions => Loc.Get(Strings.Media.Captions);
    public static string PreviousChapter => Loc.Get(Strings.Media.PreviousChapter);
    public static string NextChapter => Loc.Get(Strings.Media.NextChapter);
    public static string Fullscreen => Loc.Get(Strings.Media.Fullscreen);
    public static string ExitFullscreen => Loc.Get(Strings.Media.ExitFullscreen);
    public const string F11 = "F11";                       // loc-allow: keyboard-accelerator key name, invariant

    public static string Seeking => Loc.Get(Strings.Media.Seeking);
    public static string ChangingQuality => Loc.Get(Strings.Media.ChangingQuality);
    public static string ChangingTrack => Loc.Get(Strings.Media.ChangingTrack);
    public static string CatchingUp => Loc.Get(Strings.Media.CatchingUp);
    public static string Reconnecting => Loc.Get(Strings.Media.Reconnecting);
    public static string Buffering => Loc.Get(Strings.Media.Buffering);
    public static string VideoFailed => Loc.Get(Strings.Media.VideoFailed);

    public static string CaptionsFor(string? label) => Loc.Format(Strings.Media.CaptionsForKey, ("label", label ?? ""));
    public static string CaptionsIndexed(int n) => Loc.Format(Strings.Media.CaptionsIndexedKey, ("n", n));
    public static string AudioIndexed(int n) => Loc.Format(Strings.Media.AudioIndexedKey, ("n", n));
    public static string QualityHeight(int height) => $"{height}p";      // loc-allow: universal resolution suffix
    public static string RateLabel(float rate) => $"{rate:0.##}×";       // loc-allow: universal multiplier symbol
}
