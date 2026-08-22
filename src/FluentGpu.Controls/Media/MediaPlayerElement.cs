using System;
using System.Threading;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls.Media;

/// <summary>When the pointer cursor may hide together with the transport chrome.</summary>
public enum CursorAutoHidePolicy : byte
{
    /// <summary>Never hide the cursor (accessibility-safe; the chrome still hides).</summary>
    Never,
    /// <summary>Hide it only in fullscreen presentation — mpv's <c>--cursor-autohide-fs-only</c> and the default.</summary>
    FullscreenOnly,
    /// <summary>Hide it whenever the chrome hides and the pointer is over the video rect (kiosk / dedicated player).</summary>
    Always,
}

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
/// <item><b>Pure Render.</b> Render has NO side effects. The event-driven video pump (viewport + <see cref="IMediaPlayer.PumpVideo"/>)
///   is a callback registered once at mount and invoked after coalesced native/geometry requests —
///   it reads the live laid-out area, so the video tracks layout without the control re-rendering.</item>
/// <item><b>No whole-player frame-clock re-render.</b> Position drives compositor binds (the seek fill/playhead in
///   <see cref="MediaSeekBar"/>) and a per-second quantized time label (<see cref="MediaTransportTime"/>); the player
///   component re-renders only on LOW-frequency signal changes, never per frame.</item>
/// <item><b>First-class fullscreen hand-off.</b> The fullscreen presentation shares the inline surface via an explicit
///   single-writer ownership transfer on the registry (<see cref="VideoSurfaceRegistry.TransferOwnership"/>) — a
///   non-owner pump is a no-op, so the two views never fight over the shared slot.</item>
/// <item><b>One auto-hide chokepoint.</b> Eleven suppressors live in ONE pure predicate (<c>CanHide</c>) and every
///   event — pointer, key, focus, menu, scrub, seek, state change — funnels into ONE method (<c>Reevaluate</c>) that
///   owns the dwell timer. Nothing else may cancel or arm it, which is what retires the whole "flyout closed but the
///   timer never restarted" bug class. The chrome stays MOUNTED across the cycle (a stable Key): visibility rides the
///   opacity + hit-test + focusability channel, so hidden chrome is out of the hit-test, focus and accessibility trees
///   and a re-reveal never rebuilds the seek bar at width 0.</item>
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
    /// <summary>The poster cross-fades OUT as the first frame lands — 150 ms, the shortest fade that still reads as a
    /// hand-off rather than a cut.</summary>
    private const float PosterCrossFadeMs = 150f;
    /// <summary>Nothing at all is drawn over the poster for this long. A spinner that flashes for 200 ms is worse than
    /// no spinner: it reports trouble that did not happen.</summary>
    private const float StartupSpinnerDelayMs = 500f;
    /// <summary>Only past this may a DETERMINATE readout appear — before it, a percentage is noise with a number on it.</summary>
    private const float StartupDetailDelayMs = 10_000f;
    /// <summary>Upper bound on the S2 buffering suppressor. Buffering must suppress hiding; it must not LATCH it (a
    /// stale 250 ms native sample used as a permanent force-show is what pinned the chrome forever on a hung start).</summary>
    private const float BufferingSuppressMaxMs = 8_000f;
    /// <summary>Force-show window after entering <see cref="PlaybackState.Opening"/> — the user gets the controls while
    /// the stream starts, and then the normal machine takes over rather than pinning forever.</summary>
    private const float OpeningForceShowMs = 3_000f;
    /// <summary>Caption baseline inset from the bottom of the video area with the chrome DOWN.</summary>
    private const float CaptionBottomMargin = 28f;
    /// <summary>Volume nudge for the Up/Down keys and the wheel (5 points, the universal player step).</summary>
    private const float VolumeStep = 0.05f;
    /// <summary>Transport compaction threshold (DIP): below it, the chips fold into the ⋯ menu.</summary>
    private const float CompactTransportWidth = 420f;

    /// <summary>The poster's hand-off to the first video frame: a straight cross-fade, no scale, no slide. Anything
    /// more is a transition ON TOP of a transition — the picture is already changing.</summary>
    private static readonly LayoutTransition PosterMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(PosterCrossFadeMs, Easing.FluentStandard),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(PosterCrossFadeMs, Easing.FluentStandard));
    /// <summary>The caption lift. Transform-only (a FLIP), on the chrome's own clock: captions MOVE out of the
    /// transport's way, the controls never move out of the captions' way.</summary>
    private static readonly LayoutTransition CaptionMotion = new(
        TransitionChannels.Position,
        TransitionDynamics.Tween(MotionTok.MediaChromeFadeOutMs, Easing.FluentStandard));
    private static readonly LayoutTransition LoadingMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.98f, Sy: 0.98f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(140f, Easing.EaseInOut));
    private static readonly ContextMenuOptions MoreMenuOptions = new()
    {
        MinWidth = MenuFlyout.ThemeMinWidth,
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedLeft,
        KeyboardPlacement = FlyoutPlacement.TopEdgeAlignedRight,
    };

    /// <summary>The player this element presents (headless contract; the MF backend drives real video).</summary>
    public required IMediaPlayer Player { get; init; }
    /// <summary>Optional host-owned play command. Apps with an authoritative playback controller should provide this so
    /// a local surface cannot bypass the controller's play-intent state. Null keeps the standalone player behavior.</summary>
    public Action? PlayRequested { get; init; }
    /// <summary>Optional host-owned pause command; see <see cref="PlayRequested"/>.</summary>
    public Action? PauseRequested { get; init; }
    /// <summary>Optional host-owned seek command. The requested mode is preserved for hosts that distinguish fast
    /// keyframe previews from accurate commits. Null seeks <see cref="Player"/> directly.</summary>
    public Action<TimeSpan, SeekMode>? SeekRequested { get; init; }
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

    /// <summary>The UI-side corner shape of the frame and the video stage. Fullscreen is square. An explicitly-set
    /// <see cref="CornerRadius"/> (&gt; 0) drives the frame regardless of <see cref="IsDecorative"/> — E2: a
    /// non-decorative (operable) player asked for e.g. <c>CornerRadius = Radii.Card</c> previously got a UI frame at
    /// the hard-coded <see cref="Radii.OverlayAll"/> while the DComp child underneath was already told
    /// <see cref="CornerRadius"/> via <see cref="VideoSurfaceRegistry.SetCornerRadius"/> (<see cref="PumpNow"/>
    /// ~:516-517) — the UI clip and the compositor clip disagreed. <see cref="CornerRadius"/> == 0 keeps BOTH
    /// pre-existing defaults unchanged: decoration still follows its own (square-by-default) shape (a decorative
    /// surface's shape IS the caller's shape), and everything else still keeps the standard overlay radius.</summary>
    private CornerRadius4 FrameCorners =>
        IsFullscreenPresentation ? default
        : CornerRadius > 0f ? CornerRadius4.All(CornerRadius)
        : IsDecorative ? CornerRadius4.All(CornerRadius)
        : Radii.OverlayAll;
    /// <summary>Show the transport controls. Never crashes across OS versions (there is no OS control). Default true.</summary>
    public bool AreTransportControlsEnabled { get; init; } = true;
    /// <summary>Hide overlay chrome after inactivity while playback advances. Pointer/focus/touch reveal it.</summary>
    public bool AutoHideTransportControls { get; init; } = true;
    /// <summary>Idle time before playing chrome fades away.</summary>
    public float TransportControlsHideDelayMs { get; init; } = MotionTok.MediaChromeIdleDelayMs;
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
    /// <summary>The host owns the transport for this session; render none. Used by Wavee's single-TransportOwner rule.
    ///
    /// <para>Distinct from <see cref="AreTransportControlsEnabled"/> = false, which says "this surface has no transport
    /// at all" (a decorative clip, a preview). This says "the transport exists, it just lives somewhere else" — so the
    /// element still runs its full keyboard map, its cursor policy and its fullscreen hand-off, it simply draws no
    /// chrome of its own. Two surfaces bound to one player must never both draw a transport: the second one is a second
    /// authority over the same playhead, and the two disagree the moment either issues a seek.</para></summary>
    public bool SuppressTransport { get; init; }
    /// <summary>Raised when this element enters (true) or leaves (false) fullscreen presentation. Fires on the SAME edge
    /// that flips the fullscreen signal, including the host-delegated path (<see cref="FullscreenRequested"/>) and the
    /// Escape/F11 key paths — a host that mirrors fullscreen into its own layout can drive off this alone.</summary>
    public Action<bool>? FullscreenChanged { get; init; }
    /// <summary>Optional host-owned picture-in-picture command (the transport's PiP affordance). The button renders only
    /// when this is set AND the player advertises <see cref="MediaCommandFlags.PictureInPicture"/> — the element never
    /// invents a window, it only asks.</summary>
    public Action? PictureInPictureRequested { get; init; }
    /// <summary>Assistive-technology probe (S11). When it returns true the chrome NEVER auto-hides: a screen-reader or
    /// switch-access user is navigating controls that a dwell timer would delete out from under them. Null (the default)
    /// reads as "no AT attached" — the engine currently exposes no UIA-client seam to poll (the Windows provider gates
    /// its own raises on <c>UiaClientsAreListening</c> internally), so a host that knows better supplies this.</summary>
    public Func<bool>? IsAccessibilityActive { get; init; }
    /// <summary>When the pointer cursor is allowed to hide with the chrome. Default <see cref="CursorAutoHidePolicy.FullscreenOnly"/>,
    /// which is mpv's <c>--cursor-autohide-fs-only</c> and what every windowed player does: hiding the cursor over a
    /// small inline video steals it from the page around it, and the user cannot tell whether the app has hung.</summary>
    public CursorAutoHidePolicy CursorAutoHide { get; init; } = CursorAutoHidePolicy.FullscreenOnly;
    /// <summary>The host is presenting this element fullscreen (its own surface, not the element's overlay path).
    /// Drives the transport glyph, the ⋯ row label and Esc handling so a host-owned fullscreen does not render a
    /// control named for the state the user is already in. Distinct from the internal overlay-owned
    /// <see cref="IsFullscreenPresentation"/>; the two are OR-ed everywhere the presentation state is consulted.</summary>
    public bool IsHostFullscreen { get; init; }

    /// <summary>The element is being PRESENTED fullscreen, by either route (its own overlay, or a host surface that set
    /// <see cref="IsHostFullscreen"/>). Every label, glyph, Escape guard and cursor-policy decision reads this, never
    /// the overlay-only flag — a control named for the state the user is already in reads as a dead button.</summary>
    private bool PresentingFullscreen => IsFullscreenPresentation || IsHostFullscreen;
    /// <summary>When set, F11 and the transport's fullscreen button DELEGATE instead of opening this element's own
    /// overlay — the host app owns where fullscreen lives. Unset keeps the standalone behaviour verbatim.</summary>
    public Action? FullscreenRequested { get; init; }
    /// <summary>Rows the HOST app contributes to the transport's More (⋯) menu. They render FIRST — above the element's own
    /// rows, with a separator between — because a host's commands are more specific than the element's generic playback
    /// settings, and on a small surface "where should this video live" is the likeliest reason to open the menu at all.
    ///
    /// <para>A <see cref="Func{T}"/> and not a list, deliberately: these rows are STATE-DEPENDENT (which option is
    /// radio-checked, which is disabled and why) and init props FREEZE AT MOUNT, so a frozen list would show a stale
    /// checkmark for the lifetime of the element — the same trap <c>SplitButton</c>'s ChecksReuse tripwire exists to catch.
    /// Invoked once per menu open, so it always reflects live state.</para>
    ///
    /// <para>The element never interprets these rows; it only renders them. It stays host-agnostic.</para></summary>
    public Func<IReadOnlyList<MenuFlyoutItem>>? MoreMenuItems { get; init; }

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
    // Native media callbacks can arrive from any thread. They post one coalesced registry request onto the UI thread.
    private Action<Action>? _postToUi;
    private readonly Action _drainPumpRequest;
    private int _pumpPostQueued;

    /// <summary>Create the control's stable UI-post drain delegate once; it is reused by every native media event.</summary>
    public MediaPlayerElement()
    {
        _drainPumpRequest = DrainPumpRequest;
        _clearMoveBurst = ClearMoveBurst;
        _resolveFocusOut = ResolveFocusOut;
    }

    private void QueuePumpRequest()
    {
        var post = _postToUi;
        if (post is null || Interlocked.Exchange(ref _pumpPostQueued, 1) != 0) return;
        post(_drainPumpRequest);
    }

    private void DrainPumpRequest()
    {
        Volatile.Write(ref _pumpPostQueued, 0);
        _binding.RequestPump();
    }

    private void RequestBindingPump() => _binding.RequestPump();

    // -----------------------------------------------------------------------------------------------------------
    //  The auto-hide state machine (Task 1).
    //
    //  ChromeState = Hidden | Visible. There is NO "Pinned" state: pinned is the DERIVED condition
    //  `Visible && !CanHide()`. CursorState is a strict SLAVE of ChromeState - it never has an opinion of its own, it
    //  only trails the chrome by MediaChromeCursorExtraDelayMs so the two disappearances do not read as one glitch.
    //
    //  Every suppressor lives in ONE pure predicate (CanHide) and every event - pointer move/press/release/enter/exit,
    //  key, focus in/out, menu open/close, scrub start/end, seek issued/settled, playback state change - funnels into
    //  ONE chokepoint (Reevaluate). That is the fix for the entire "flyout closed but the timer never restarted" bug
    //  class: there is no code path that cancels the timer without a matching path that re-arms it, because there is
    //  only one path.
    //
    //  These are FIELDS, not render locals: the timer callbacks and the element's stable event delegates must reach the
    //  same state without re-creating a closure per render, and most of the state (pointer, focus, menu) is not
    //  render-visible at all - writing it to a signal would re-render the player for a pointer move.
    // -----------------------------------------------------------------------------------------------------------

    private Signal<bool>? _chromeVisible;
    private InputHooks? _hooks;
    private Signal<bool>? _fullscreenState;
    private MediaSeekBar? _seekBar;
    private TimerHandle _hideFast, _hideSlow, _cursorTimer;
    private bool _hideArmed;

    // S10/S11 - settings + assistive tech.
    private bool _autoHideEnabled, _accessibilityActive;
    // S1/S2/S9 - model state (written from Render, which already reads these signals).
    private bool _notPlaying, _buffering, _seekInFlight, _audioOnlyOrError, _forceShow;
    // S3/S4 - pointer.
    private bool _pointerOverChrome, _pointerDown, _pointerInVideo;
    // S6/S7/S8 - menu, focus, move burst.
    private bool _menuOpen, _focusInChrome, _moveBurstActive, _focusOutPending;
    // Pointer de-duplication + the move threshold (NaN = no previous sample).
    private float _lastMoveX = float.NaN, _lastMoveY = float.NaN;
    // Which dwell applies: touch and focus reveals get the longer one (neither can re-arm by hovering).
    private bool _revealedByTouchOrFocus;
    private bool _cursorHidden;
    private bool _moveBurstPostQueued;
    private NodeHandle _playerRoot;
    private readonly Action _clearMoveBurst;
    private readonly Action _resolveFocusOut;

    /// <summary>The ONE predicate. Eleven clauses, no side effects, no allocation - WinUI's
    /// <c>ShouldHideControlPanel</c> and Chromium's <c>ShouldHideMediaControls</c> are the same shape and the same
    /// length, because a player really does have this many reasons not to take its controls away.</summary>
    private bool CanHide()
        => _autoHideEnabled && !_accessibilityActive                           // S10 setting, S11 UIA client attached
        && !_notPlaying && !_buffering && !_seekInFlight && !_audioOnlyOrError  // S1, S2, S9
        && !_forceShow
        && !_pointerOverChrome && !_pointerDown && !Scrubbing()                // S3, S4, S5
        && !_menuOpen && !_focusInChrome && !_moveBurstActive;                 // S6, S7, S8

    /// <summary>S5 - the scrub gate, owned by <see cref="MediaSeekBar.Scrubbing"/>: closed from pointer-down until the
    /// player CONFIRMS the committed seek, not merely until pointer-up.</summary>
    private bool Scrubbing() => _seekBar is { } bar && bar.Scrubbing.Peek();

    /// <summary>THE chokepoint. Not-hideable then cancel + show (chrome AND cursor). Hideable and visible with no timer
    /// running then start the dwell. Idempotent, so any event may call it - and every event does.</summary>
    private void Reevaluate()
    {
        if (!CanHide())
        {
            CancelHide();
            ShowChrome();
            ShowCursor();
            return;
        }
        if (_chromeVisible is { } v && v.Peek() && !_hideArmed) ArmHide();
    }

    /// <summary>Idempotent reveal. Deliberately does NOT start the timer - that is <see cref="Reevaluate"/>'s job, and
    /// splitting them is what lets the timer path call "show" without re-arming itself.</summary>
    private void ShowChrome()
    {
        if (!AreTransportControlsEnabled || SuppressTransport) return;
        if (_chromeVisible is { } v) v.Value = true;   // value-gated: no re-render when already visible
    }

    private void CancelHide()
    {
        _hideArmed = false;
        _hideFast.Cancel();
        _hideSlow.Cancel();
        _cursorTimer.Cancel();
    }

    private void ArmHide()
    {
        _hideArmed = true;
        // Exactly one of the two dwells is live at a time. Two hooks rather than one variable-delay hook because
        // TimerHandle.Restart() re-arms from the hook's DECLARED duration - a runtime delay would need a re-render.
        if (_revealedByTouchOrFocus) { _hideFast.Cancel(); _hideSlow.Restart(); }
        else { _hideSlow.Cancel(); _hideFast.Restart(); }
    }

    private void OnHideDue()
    {
        _hideArmed = false;
        if (!CanHide()) { Reevaluate(); return; }        // a suppressor appeared while the dwell ran
        if (_chromeVisible is { } v) v.Value = false;
        _cursorTimer.Restart();                          // the cursor trails the chrome, it does not race it
    }

    private void OnCursorDue()
    {
        if (_chromeVisible is { } v && v.Peek()) return;   // chrome came back - the cursor follows it
        if (!_pointerInVideo || !CursorHidingAllowed()) return;
        HideCursor();
    }

    private bool CursorHidingAllowed() => CursorAutoHide switch
    {
        CursorAutoHidePolicy.Always => true,
        CursorAutoHidePolicy.FullscreenOnly => IsFullscreenNow(),
        _ => false,
    };

    private bool IsFullscreenNow() => PresentingFullscreen || (_fullscreenState is { } f && f.Peek());

    private void HideCursor()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        _hooks?.SetCursorOverride?.Invoke(this, CursorId.Hidden);
    }

    private void ShowCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        _hooks?.SetCursorOverride?.Invoke(this, null);
    }

    private void ClearMoveBurst()
    {
        _moveBurstPostQueued = false;
        if (!_moveBurstActive) return;
        _moveBurstActive = false;
        Reevaluate();
    }

    /// <summary>LostFocus fires BEFORE the next GotFocus, so a Tab between two transport buttons momentarily reports
    /// "focus left the chrome". Deferring one dispatcher tick lets the paired GotFocus cancel the departure.</summary>
    private void ResolveFocusOut()
    {
        if (!_focusOutPending) return;
        _focusOutPending = false;
        _focusInChrome = false;
        Reevaluate();
    }

    /// <summary>S7 - KEYBOARD focus inside the chrome suppresses hiding. Revealing on focus (the previous behaviour)
    /// without suppressing is a WCAG 2.4.7 failure: the focused control fades out from under the keyboard user.
    /// <para>But only KEYBOARD focus may pin. Focus also arrives here by pointer activation and, more importantly, by
    /// the overlay service RESTORING focus to the invoking button when a picker closes - and treating that as a
    /// keyboard user would pin the chrome open forever after any mouse trip through the quality menu, which is the
    /// opposite of what every shipped player does. <see cref="NodeFlags.FocusVisual"/> is exactly this distinction
    /// (<c>InputDispatcher.SetFocus</c>'s <c>visual</c> argument: true for Tab/arrow navigation, false for pointer and
    /// programmatic moves) and its doc names reading it from a GotFocus handler as the sanctioned use.</para></summary>
    private void OnChromeFocusChanged(bool focused)
    {
        if (focused)
        {
            _focusOutPending = false;
            _focusInChrome = IsKeyboardFocus();
            // A keyboard user gets the longer dwell: no pointer motion is coming to re-arm it. A pointer or restored
            // focus keeps the normal one, so the chrome still collapses after a mouse trip through a picker.
            if (_focusInChrome) _revealedByTouchOrFocus = true;
            ShowChrome();
            ShowCursor();
            Reevaluate();
            return;
        }
        _focusOutPending = true;
        if (_postToUi is { } post) post(_resolveFocusOut); else ResolveFocusOut();
    }

    /// <summary>Whether the CURRENTLY focused node carries the engine's keyboard-focus visual. False when nothing is
    /// focused, when the scene is unavailable, or when focus arrived by pointer/programmatic move.</summary>
    private bool IsKeyboardFocus()
    {
        if (_hooks?.GetFocus?.Invoke() is not { } node || node.IsNull) return false;
        var scene = _scene;
        if (scene is null || !scene.IsLive(node)) return false;
        return (scene.Flags(node) & NodeFlags.FocusVisual) != 0;
    }

    /// <summary>Pointer move over the player. De-duplicates identical coordinates FIRST (the video.js phantom-mousemove
    /// fix: the platform re-delivers the last position on unrelated events, which alone keeps a player's chrome up
    /// forever), then applies the movement threshold - but only while HIDDEN. Once the chrome is up, any move re-arms.</summary>
    private void OnPointerMoved(Point2 p)
    {
        _pointerInVideo = true;
        if (p.X == _lastMoveX && p.Y == _lastMoveY) return;              // phantom move - drop the event entirely
        bool hidden = _chromeVisible is { } v && !v.Peek();
        if (hidden && !float.IsNaN(_lastMoveX))
        {
            float dx = p.X - _lastMoveX, dy = p.Y - _lastMoveY;
            const float th = MotionTok.MediaChromeMoveThresholdDip;
            if (dx * dx + dy * dy < th * th) { _lastMoveX = p.X; _lastMoveY = p.Y; return; }
        }
        _lastMoveX = p.X; _lastMoveY = p.Y;
        _revealedByTouchOrFocus = false;
        _moveBurstActive = true;                                          // WinUI m_isPointerMove
        ShowCursor();
        ShowChrome();
        Reevaluate();
        if (_moveBurstPostQueued) return;
        _moveBurstPostQueued = true;
        if (_postToUi is { } post) post(_clearMoveBurst); else ClearMoveBurst();
    }

    private Signal<bool>? _releaseBufferSuppress;
    private Signal<bool>? _openingGrace;
    private Signal<int>? _startupPhase;

    private bool ChromeIsVisible() => _chromeVisible is { } v && v.Peek();

    /// <summary>The S2 suppressor's own release. Buffering is allowed to hold the chrome, but only for a bounded time —
    /// after that the machine goes back to asking CanHide() like everything else.</summary>
    private void OnSuppressorExpired()
    {
        if (_releaseBufferSuppress is { } sig) sig.SetIfChanged(false);
        _buffering = false;
        _seekInFlight = false;
        Reevaluate();
    }

    private void OnOpeningGraceExpired()
    {
        if (_openingGrace is { } sig) sig.SetIfChanged(false);
        _forceShow = false;
        Reevaluate();
    }

    private void OnStartupSpinnerDue() => _startupPhase?.SetIfChanged(1);
    private void OnStartupDetailDue() => _startupPhase?.SetIfChanged(2);

    /// <summary>Mount: every UseTimeout arms itself when its cell is created, so hand the machine a clean slate and let
    /// it decide. Nothing else in the element is allowed to start or stop a dwell.</summary>
    private void OnMounted()
    {
        CancelHide();
        _startupPhase?.SetIfChanged(0);
        Reevaluate();
    }

    private void ReleaseCursorOverride() => _hooks?.SetCursorOverride?.Invoke(this, null);

    /// <summary>S6 — "is ANY input-blocking overlay up right now". The overlay service knows; the interface does not yet
    /// surface it, and <c>OverlayServiceImpl</c> is internal to this very assembly, so the kit reads it directly. A
    /// host-less tree (the null service) answers false, which is the right answer there.</summary>
    private static bool AnyInputBlockingOverlay(IOverlayService service)
        => service is OverlayServiceImpl impl && impl.AnyInputBlocking;

    private void AdjustVolume(float delta)
    {
        float v = Math.Clamp(Player.Volume.Peek() + delta, 0f, 1f);
        Player.SetVolume(v);
        if (delta > 0f && Player.Muted.Peek()) Player.SetMuted(false);   // raising the volume un-mutes (WinUI/YouTube)
    }

    /// <summary>Rule 1 of the transport's input contract, and the single most preventable anger class in a video UI
    /// (Jellyfin #1079 / #2376 / #2058): a transport button must NEVER keep keyboard focus after a MOUSE activation, or
    /// the next Space re-activates the last-clicked button instead of toggling play. <c>AllowFocusOnInteraction = false</c>
    /// stops the button TAKING focus; this hands focus BACK to the video surface, so a click on the chrome leaves the
    /// keyboard pointed at the player even when focus was somewhere else in the app entirely.</summary>
    private void OnChromePointerPressed(PointerEventArgs e)
    {
        if (e.Button != 0) return;
        if (_playerRoot.IsNull) return;
        _hooks?.FocusNode?.Invoke(_playerRoot, false);
    }

    private void OnChromePointerMove(Point2 _)
    {
        if (_pointerOverChrome) return;
        _pointerOverChrome = true;    // S3 - over the CONTROL PANEL, not merely over the element
        Reevaluate();
    }

    private void OnChromePointerExit()
    {
        if (!_pointerOverChrome) return;
        _pointerOverChrome = false;
        Reevaluate();
    }

    public override Element Render()
    {
        // IsFullscreenPresentation freezes at mount, so this conditional hook is stable for the instance's lifetime.
        var binding = IsFullscreenPresentation ? PresentationBinding : UseVideoSurface();
        _postToUi = UsePost();
        var hooks = UseContext(InputHooks.Current);
        var overlayService = UseContext(Overlay.Service);
        _hooks = hooks;
        var areaRef = UseRef<NodeHandle>(default);
        var holeRef = UseRef<NodeHandle>(default);
        var playerRoot = UseRef<NodeHandle>(default);
        var chromeRef = UseRef<NodeHandle>(default);
        var ccAnchor = UseRef<NodeHandle>(default);
        var qualityAnchor = UseRef<NodeHandle>(default);
        var rateAnchor = UseRef<NodeHandle>(default);
        var audioAnchor = UseRef<NodeHandle>(default);
        var fullscreenHandle = UseRef<OverlayHandle?>(null);
        var localFullscreen = UseSignal(false);
        var fullscreen = FullscreenState ?? localFullscreen;
        _fullscreenState = fullscreen;
        var localAspect = UseSignal(ToAspectMode(Stretch));
        var localCustomAspect = UseSignal(16.0 / 9.0);
        var aspectSig = AspectMode ?? localAspect;                 // materialized controlled signals (no write-sniffing)
        var customAspectSig = CustomAspectRatio ?? localCustomAspect;
        var chromeVisible = UseSignal(true);
        var chromeHeight = UseSignal(0f);
        var volumeExpanded = UseSignal(false);
        var shortcutsOpen = UseSignal(false);
        var areaBounds = UseSignal<RectF>(default);                // video-area bounds (bounds-changed column -> letterbox recompute)
        _chromeVisible = chromeVisible;

        // The seek bar is OWNED by this element rather than conjured inside an Embed factory: the auto-hide machine has
        // to read its scrub gate (S5) and the time label has to read its scrub target, and neither can reach an instance
        // that only exists inside a closure. Created once, reused for the element's whole life.
        var seekBarRef = UseRef<MediaSeekBar?>(null);
        seekBarRef.Value ??= new MediaSeekBar
        {
            Player = Player,
            SeekRequested = SeekRequested,
            ChromeVisible = chromeVisible,
        };
        MediaSeekBar seekBar = seekBarRef.Value!;
        _seekBar = seekBar;

        var natural = Player.NaturalSize.Value;
        var state = Player.State.Value;
        bool playIntent = Player.IsPlayRequested.Value;
        var bufferingInfo = Player.Buffering.Value;
        TimedCue? activeCue = Player.ActiveCue.Value;
        VideoAspectMode aspect = aspectSig.Value;
        double customAspect = customAspectSig.Value;
        bool audioOnly = IsAudioOnly(natural);
        bool videoReady = !audioOnly && state is not (PlaybackState.Idle or PlaybackState.Opening);

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
            int reg = binding.RegisterPump(this, PumpNow);   // engine invokes PumpNow after a coalesced request
            return () => binding.UnregisterPump(reg);
        }, DepKey.Empty);

        // The source raises from its native/media thread; QueuePumpRequest posts exactly one UI-thread registry request.
        // Headless players do not implement IVideoPumpSource, but registration still gives them their initial geometry pump.
        UseEffect(() =>
        {
            if (Player is not IVideoPumpSource source) return (Action?)null;
            source.PumpRequested += QueuePumpRequest;
            return () => source.PumpRequested -= QueuePumpRequest;
        }, DepKey.Empty);

        // A KeepAlive/window activation edge and any source/fit change need one fresh settled placement, never a
        // permanent pump. Bounds changes below cover resizes/scroll geometry without re-rendering this component.
        UseActivation(RequestBindingPump, RequestBindingPump);
        int pumpGeometryKey = HashCode.Combine(HashCode.Combine(natural, aspect), customAspect);
        UseEffect(RequestBindingPump, pumpGeometryKey);

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

        // ── S2/S9: bounded buffering suppression ─────────────────────────────────────────────────────────────────────
        // A rebuffer must suppress hiding, but it must NOT LATCH it. The protected session maps both Licensed and
        // Buffering onto PlaybackState.Buffering and samples native state only every 250 ms, so a stale sample used as a
        // permanent force-show pinned the chrome forever on any hung start. The suppressor is therefore time-bounded:
        // it holds while buffering is reported and for at most BufferingSuppressMaxMs, then releases on its own.
        bool bufferingNow = bufferingInfo.IsBuffering || state is PlaybackState.Buffering or PlaybackState.Stalled;
        var bufferSuppress = UseSignal(false);
        var bufferTimer = UseTimeout(OnSuppressorExpired, BufferingSuppressMaxMs, DepKey.Empty);
        _releaseBufferSuppress = bufferSuppress;
        UseEffect(() =>
        {
            if (bufferingNow) { bufferSuppress.Value = true; bufferTimer.Restart(); }
            else { bufferTimer.Cancel(); bufferSuppress.Value = false; Reevaluate(); }
            return (Action?)null;
        }, bufferingNow ? 1 : 0);

        // ── S1 force-show grace: the first OpeningForceShowMs after Opening. Force-showing for the WHOLE of a slow open
        //    is what made a stuck stream pin its chrome; a bounded grace gives the user the controls while the stream
        //    starts and then lets the normal machine take over.
        var openingGrace = UseSignal(false);
        var openingTimer = UseTimeout(OnOpeningGraceExpired, OpeningForceShowMs, DepKey.Empty);
        _openingGrace = openingGrace;
        UseEffect(() =>
        {
            if (state == PlaybackState.Opening) { openingGrace.Value = true; openingTimer.Restart(); }
            // LEAVING Opening must RELEASE the grace, exactly as the buffering suppressor above releases its own.
            // Letting it lapse on the timer alone pinned the chrome for the full OpeningForceShowMs after playback had
            // already started — so on a fast open (the common case) the controls sat there for three seconds no matter
            // what the user did, which is the "auto-collapse does not work" report in its purest form.
            else { openingTimer.Cancel(); openingGrace.Value = false; Reevaluate(); }
            return (Action?)null;
        }, state == PlaybackState.Opening ? 1 : 0);

        // ── the eleven suppressors, published to the state machine ───────────────────────────────────────────────────
        // gate.media.el.transport-suppressed reads this exact spelling: auto-hide is gated on the transport existing.
        bool autoHideArmed = AreTransportControlsEnabled && AutoHideTransportControls && !SuppressTransport && !IsDecorative;
        _autoHideEnabled = autoHideArmed;
        _accessibilityActive = IsAccessibilityActive?.Invoke() ?? false;
        // S1 is a REAL, user-visible stop — not "anything that is not Playing". Buffering is S2, Opening is the grace.
        _notPlaying = !playIntent || IsStoppedState(state);
        _buffering = bufferSuppress.Value;
        _seekInFlight = bufferSuppress.Value && bufferingInfo.Reason == BufferingReason.Seeking;
        _audioOnlyOrError = audioOnly || state == PlaybackState.Failed;
        _forceShow = openingGrace.Value;
        bool forceChrome = ShouldForceChrome(playIntent, state) || _forceShow;
        bool showChrome = AreTransportControlsEnabled && !SuppressTransport && (forceChrome || chromeVisible.Value);

        // ONE chokepoint, re-entered whenever a render-visible suppressor changes. A pin FLAP inside a single flush is
        // harmless now: the machine is level-triggered (it asks CanHide()), never edge-triggered on a Cancel/Restart pair.
        UseEffect(Reevaluate, HashCode.Combine(
            HashCode.Combine(_autoHideEnabled, _accessibilityActive, _notPlaying, _buffering),
            HashCode.Combine(_seekInFlight, _audioOnlyOrError, _forceShow, showChrome)));

        // The scrub gate (S5) lives in the seek bar and changes without re-rendering this element: track it in an
        // EFFECT so a scrub start/end re-evaluates the machine at zero render cost.
        UseSignalEffect(() => { _ = seekBar.Scrubbing.Value; Reevaluate(); });

        // S6 — menu. Two fixes in one: (i) suppress while ANY input-blocking overlay is up, because such an overlay
        // mounts a full-bleed scrim that becomes the topmost hit target, so OnPointerMoveWithin stops firing while
        // IsAnchorPinned(playerRoot) stays false and the dwell fires UNDER the menu; (ii) the epoch drives an EFFECT,
        // never the render — subscribing to the host-global PinEpoch in Render re-rendered the whole player twice for
        // every menu opened anywhere in the shell. Nothing here re-renders; it only re-evaluates.
        UseSignalEffect(() =>
        {
            _ = overlayService.PinEpoch.Value;      // the change signal (not the decision)
            bool blocking = AnyInputBlockingOverlay(overlayService);
            if (blocking == _menuOpen) return;
            _menuOpen = blocking;
            Reevaluate();
        });

        // The two dwells + the cursor trail. Exactly one dwell is armed at a time (see ArmHide).
        _hideFast = UseTimeout(OnHideDue, MathF.Max(MotionTok.MediaChromeFadeOutMs, TransportControlsHideDelayMs), DepKey.Empty);
        _hideSlow = UseTimeout(OnHideDue, MathF.Max(TransportControlsHideDelayMs,
            MathF.Max(MotionTok.MediaChromeIdleDelayTouchMs, MotionTok.MediaChromeIdleDelayAfterFocusMs)), DepKey.Empty);
        _cursorTimer = UseTimeout(OnCursorDue, MotionTok.MediaChromeCursorExtraDelayMs, DepKey.Empty);
        // UseTimeout arms at mount; the machine, not the hook, decides whether a dwell should be running.
        UseEffect(OnMounted, DepKey.Empty);
        UseEffect(() => (Action?)ReleaseCursorOverride, DepKey.Empty);

        // Entering fullscreen applies the cursor policy IMMEDIATELY — waiting for a move means the cursor sits on top of
        // a fullscreen frame until the user jiggles it, which is exactly when they are least likely to.
        bool presentedFullscreen = PresentingFullscreen || fullscreen.Value;   // subscribe: the policy edge must re-render
        UseEffect(() => { if (!ChromeIsVisible()) _cursorTimer.Restart(); else ShowCursor(); return (Action?)null; },
            presentedFullscreen ? 1 : 0);

        // ── startup / first frame (Task 5) ───────────────────────────────────────────────────────────────────────────
        // 0 ms poster. 0-500 ms NOTHING (a spinner that flashes for 200 ms is worse than no spinner). 500 ms an animated
        // indeterminate ring. Past 10 s a determinate readout may appear. A mid-play REBUFFER is not a startup: it never
        // reaches this ladder, never shows the poster, and never resets the chrome.
        var startupPhase = UseSignal(0);
        _startupPhase = startupPhase;
        var startupSpinner = UseTimeout(OnStartupSpinnerDue, StartupSpinnerDelayMs, DepKey.Empty);
        var startupDetail = UseTimeout(OnStartupDetailDue, StartupDetailDelayMs, DepKey.Empty);
        bool startingUp = !IsDecorative && !videoReady && (playIntent || state is PlaybackState.Opening or PlaybackState.Buffering);
        UseEffect(() =>
        {
            if (startingUp) { startupSpinner.Restart(); startupDetail.Restart(); }
            else { startupSpinner.Cancel(); startupDetail.Cancel(); startupPhase.SetIfChanged(0); }
            return (Action?)null;
        }, startingUp ? 1 : 0);
        int phase = startupPhase.Value;

        void RevealChrome()
        {
            ShowChrome();
            ShowCursor();
            Reevaluate();
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
            FullscreenChanged?.Invoke(false);
            ShowCursor();                                          // windowed default policy never hides the cursor
            RevealChrome();
        }

        void ToggleFullscreen()
        {
            if (IsFullscreenPresentation) { ExitFullscreen?.Invoke(); return; }
            if (FullscreenRequested is { } request) { request(); FullscreenChanged?.Invoke(!(PresentingFullscreen || fullscreen.Peek())); return; }
            if (fullscreen.Peek()) { LeaveFullscreen(); return; }
            fullscreen.Value = true;
            hooks?.WindowSetFullscreen?.Invoke(true);
            var h = overlayService.OpenAt(
                static () => new RectF(0, 0, 1, 1),
                () => Embed.Comp(() => new FullscreenMediaView
                {
                    Player = Player,
                    PlayRequested = PlayRequested,
                    PauseRequested = PauseRequested,
                    SeekRequested = SeekRequested,
                    Binding = binding,
                    AspectMode = aspectSig,
                    CustomAspectRatio = customAspectSig,
                    AspectModeChanged = AspectModeChanged,
                    FullscreenState = fullscreen,
                    Exit = LeaveFullscreen,
                    LetterboxColor = LetterboxColor,
                    CursorAutoHide = CursorAutoHide,
                }),
                FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal));
            fullscreenHandle.Value = h;
            h.ClosedAction = LeaveFullscreen;
            FullscreenChanged?.Invoke(true);
        }

        // Only exits. Escape must NEVER enter fullscreen and must never quit the app: on every shipped player it is the
        // one key a panicking user reaches for, and the only thing it is allowed to do is give the window back.
        void ExitFullscreenOnly()
        {
            if (IsFullscreenPresentation) { ExitFullscreen?.Invoke(); return; }
            // Host-owned fullscreen leaves through the HOST, never through the element's overlay path: the host put us
            // there and only it knows how to put us back.
            if (IsHostFullscreen) { FullscreenRequested?.Invoke(); FullscreenChanged?.Invoke(false); return; }
            if (fullscreen.Peek()) LeaveFullscreen();
        }

        // A terminal failure wins over the opening spinner: otherwise a Failed state with a lingering play intent would
        // keep showing "Starting playback…" forever (the DRM-license-rejected infinite-spinner bug).
        // Decoration never reports status.
        Element? statusOverlay = IsDecorative
            ? null
            : state == PlaybackState.Failed
            ? FailedOverlay(Player.Error.Value?.Message)
            : startingUp
                ? (phase == 0 ? null : OpeningOverlay(playIntent, phase == 2 ? bufferingInfo.Percent : -1.0))
                : bufferingInfo.IsBuffering || state is PlaybackState.Buffering or PlaybackState.Stalled
                    ? BufferingOverlay(bufferingInfo)
                    : null;

        // ── the video stage (a ZStack: children paint in author order) ───────────────────────────────────────────────
        // Painter order once the frame is live:
        //   [0] the OPAQUE LetterboxColor stage fill across the WHOLE video area. This fill IS the letterbox — there
        //       are no separate bar elements any more.
        //   [1] the VIDEO HOLE PUNCH (DrawOp.DrawVideo, gpu-renderer.md §7.3), laid out at EXACTLY the fitted video
        //       rect. It paints nothing — it ERASES everything already recorded beneath it toward premultiplied zero,
        //       so the DComp video visual composited z-BELOW the premultiplied UI swapchain shows through at full
        //       strength instead of blending with what stayed in the back buffer.
        //   [2…] status / caption / transport overlays — LATER siblings, so they repaint over the video.
        // ONE SOURCE OF TRUTH: PumpNow places the DComp visual from scene.AbsoluteRect of the HOLE node, so the erased
        // region and the presented video are the same rect BY CONSTRUCTION.
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
                OnRealized = h => { holeRef.Value = h; binding.RequestPump(); },
            });
        }
        else if (IsDecorative)
            // Transparent until the first frame lands, so the caller's own still stays visible underneath.
            videoChildren.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
        else
            // The poster stays up for the WHOLE start — including the quiet first 500 ms and the spinner phase. It is
            // never replaced by a black rect: in a music app the poster IS the album art already on screen, and
            // swapping it for darkness to host a spinner is a visible regression, not a loading state. It cross-fades
            // out over PosterCrossFadeMs when the first frame lands (its Exit terminal).
            videoChildren.Add(new BoxEl
            {
                Key = "media-poster",
                Grow = 1f, ZStack = true, Direction = 1,
                HitTestVisible = false,
                Animate = PosterMotion,
                Children = [PosterContent ?? DefaultPoster()],
            });

        if (statusOverlay is not null)
            videoChildren.Add(new BoxEl
            {
                Key = videoReady ? "media-buffering" : "media-opening",
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Animate = LoadingMotion,
                Children = [statusOverlay],
            });
        // Captions MOVE, controls do not: the caption baseline lifts by the chrome's measured height while the chrome
        // is up and settles back when it hides, animated on the same clock (a transform-only FLIP — no relayout churn).
        if (videoReady && activeCue is { } cue)
            videoChildren.Add(CaptionOverlay(cue, CaptionBottomMargin + (showChrome ? chromeHeight.Value : 0f)));

        var videoArea = new BoxEl
        {
            ZStack = true,
            Direction = 1,
            Grow = 1f,
            MinHeight = IsFullscreenPresentation || IsDecorative ? 0f : 160f,
            ClipToBounds = true,
            Corners = FrameCorners,
            Fill = ColorF.Transparent,
            OnRealized = h => { areaRef.Value = h; binding.RequestPump(); },
            OnBoundsChanged = b =>
            {
                if (b != areaBounds.Peek()) areaBounds.Value = b;
                binding.RequestPump();
            },   // resize → recompute letterbox + one settled video placement
            Children = videoChildren.ToArray(),
        };

        var layers = new System.Collections.Generic.List<Element>(4) { videoArea };
        if (AreTransportControlsEnabled && !SuppressTransport)
        {
            // The chrome stays MOUNTED across the whole hide cycle, with a STABLE Key. Toggling it in and out of the
            // layer list unmounted the entire subtree — including the MediaSeekBar — so every re-reveal built a FRESH
            // bar with width 0 and fraction 0: the thumb sat at the far left and an empty rail flashed for at least one
            // frame on EVERY auto-hide cycle. Visibility now rides the opacity + hit-test + focusability channel, which
            // is also what keeps hidden chrome out of the hit-test, focus and accessibility trees rather than merely
            // transparent (an invisible-but-hittable control panel eats clicks meant for the video).
            layers.Add(new BoxEl
            {
                Key = "media-chrome",
                Grow = 1f,
                Direction = 1,
                Justify = FlexJustify.End,
                HitTestPassThrough = true,
                HitTestVisible = showChrome,
                Opacity = showChrome ? 1f : 0f,
                OnRealized = h => chromeRef.Value = h,
                Children = [BuildTransport(area.W, showChrome, seekBar, chromeHeight, volumeExpanded,
                    ToggleFullscreen, ccAnchor, qualityAnchor, rateAnchor, audioAnchor, overlayService)],
            });
        }
        if (shortcutsOpen.Value) layers.Add(ShortcutOverlay(() => shortcutsOpen.Value = false));

        // The fade itself: asymmetric by token (reveal 100 ms, conceal 200 ms), reduced-motion resolved as a VALUE by
        // MotionTokenDef.EffectiveDurationMs — never as a branch here. Seeding FROM the live composited opacity means a
        // reveal that interrupts a conceal starts where the pixels actually are.
        UseLayoutEffect(() =>
        {
            var node = chromeRef.Value;
            var scene = Context.Scene;
            if (Context.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
            var tok = showChrome ? MotionTok.MediaChromeReveal : MotionTok.MediaChromeConceal;
            float ms = tok.EffectiveDurationMs(AnimChannel.Opacity);
            if (ms <= 0f) return;    // reduced motion: the static Opacity above already IS the terminal value
            anim.SeedEased(node, AnimChannel.Opacity, scene.Paint(node).Opacity, showChrome ? 1f : 0f, ms, tok.Easing);
        }, showChrome ? 1 : 0);

        void HandleKey(KeyEventArgs e) => HandleKeyCore(e, seekBar, ToggleFullscreen, ExitFullscreenOnly, shortcutsOpen);

        void HandlePress(PointerEventArgs e)
        {
            _pointerDown = e.Button == 0;
            _pointerInVideo = true;
            _revealedByTouchOrFocus = e.Kind == PointerKind.Touch;
            ShowCursor();
            // Middle-button = mute (delivered on release with Button == 2, the WinUI commit-on-release shape).
            if (e.Button == 2) { Player.SetMuted(!Player.Muted.Peek()); ShowChrome(); Reevaluate(); return; }
            // A DOUBLE click toggles fullscreen. A SINGLE click deliberately does NOT toggle play: the alternative is a
            // delayed single-click that costs GetDoubleClickTime() (500 ms on Windows) on EVERY play/pause and produces
            // the notorious "video toggles state while going fullscreen" bug. YouTube, Netflix and Vimeo all chose
            // reveal-only; play/pause lives on the button, Space and K.
            if (e.ClickCount >= 2) { ToggleFullscreen(); e.Handled = true; return; }
            ShowChrome();
            Reevaluate();
        }

        void HandleRelease(PointerEventArgs e)
        {
            if (!_pointerDown) return;
            _pointerDown = false;
            Reevaluate();
        }

        void HandleExit()
        {
            _pointerInVideo = false;
            _pointerDown = false;
            _pointerOverChrome = false;
            _lastMoveX = float.NaN; _lastMoveY = float.NaN;
            ShowCursor();          // the cursor must never stay hidden outside the video rect
            Reevaluate();
        }

        void HandleWheel(WheelEventArgs e)
        {
            // Wheel over the VIDEO is volume (the universal player gesture); Shift+wheel seeks. The seek bar consumes
            // its own wheel first, so a wheel over the rail never reaches here.
            if ((e.Mods & KeyModifiers.Shift) != 0) seekBar.SeekBy(e.Delta > 0f ? 10f : -10f);
            else AdjustVolume(e.Delta > 0f ? VolumeStep : -VolumeStep);
            ShowChrome();
            Reevaluate();
            e.Handled = true;
        }

        var frame = new BoxEl
        {
            ZStack = true,
            Grow = 1f,
            Corners = FrameCorners,
            ClipToBounds = true,
            BorderColor = IsFullscreenPresentation || IsDecorative ? ColorF.Transparent : Tok.StrokeFlyoutDefault,
            BorderWidth = IsFullscreenPresentation || IsDecorative ? 0f : 1f,
            Focusable = true,
            OnRealized = h => { playerRoot.Value = h; _playerRoot = h; },
            OnKeyDown = HandleKey,
            OnPointerMoveWithin = OnPointerMoved,
            OnPointerPressed = HandlePress,
            OnPointerReleased = HandleRelease,
            OnPointerExit = HandleExit,
            OnPointerWheel = HandleWheel,
            OnFocusChanged = focused => { if (focused) RevealChrome(); },
            Children = layers.ToArray(),
        };
        // One menu, every gesture. The context-request event supplies the LIVE source/owner node, so the More button
        // never depends on an OnRealized handle that can go stale across a transport re-render (the origin-flyout bug).
        // A right-click opens THIS menu and never pauses — there is no click-to-pause to trip over.
        // Decorative clips are not operable players and deliberately expose no playback menu.
        return IsDecorative
            ? frame
            : frame.WithContextMenu(overlayService,
                () => new ContextMenuModel(BuildMoreMenuItems(aspectSig, customAspectSig, SetAspect, ToggleFullscreen)),
                MoreMenuOptions);
    }


    /// <summary>The engine-invoked on-demand video pump (registered at mount; see <see cref="VideoPump"/>). Reads the
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
        // The node whose absolute rect the surface must FOLLOW. Registered from here rather than at realize time
        // because this is the one place that already decides hole-vs-area, so a hole that appears later (videoReady
        // flipping) upgrades the tracked node for free. The registry value-gates it, so re-affirming costs nothing.
        NodeHandle geom = h;
        if (!audioOnly)
        {
            // Fallback while the hole is not (yet) realized — the first frame after the stream goes ready, or any frame
            // with no hole at all: recompute the fit so the pump never goes dark.
            VideoAspectMode mode = _aspectForPump?.Peek() ?? VideoAspectMode.Uniform;
            double customAspect = _customAspectForPump?.Peek() ?? (16.0 / 9.0);
            NodeHandle hole = _holeRef?.Value ?? default;
            bool live = !hole.IsNull && scene.IsLive(hole);
            videoRect = live ? scene.AbsoluteRect(hole) : default;
            if (live) geom = hole;
            if (!live || videoRect.W <= 0f || videoRect.H <= 0f)
                videoRect = FitVideoRect(area, natural, mode, customAspect);
            else if (mode == VideoAspectMode.UniformToFill)
            {
                // CENTER-CROP is the one mode whose fitted rect deliberately OVERFLOWS the stage: the frame is scaled
                // until it covers the area and the excess is clipped (by the viewport, below). The hole node carries
                // that overflow as NEGATIVE margins (LetterboxInsets is signed for exactly this), but a layout that
                // clamps a negative margin to zero hands back the stage rect itself — and placing the video at the
                // stage rect scales the frame DOWN to fit instead of cropping it, which is the crop mode silently
                // behaving like Fill. Recomputing the fit here is right either way: when the margins do survive
                // layout this is the same rect the hole already has.
                videoRect = FitVideoRect(area, natural, mode, customAspect);
                geom = h;   // the rect now derives from the AREA, so follow the area's geometry
            }
        }
        // Track geometry so a compositor-only move (a PiP drag, a page transition) re-places the DComp child on the
        // frame it moves, instead of waiting for an unrelated native/transport event to request the next pump.
        b.SetGeometryNode(geom);
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
        Player.SetAdaptiveViewportHeight((int)MathF.Ceiling(videoRect.H * MathF.Max(1f, s)));
        Player.PumpVideo(b, videoRect, s);
        if (audioOnly) b.SetVisible(false);
    }

    /// <summary>Intersect <paramref name="rect"/> with the bounds of every <c>ClipsToBounds</c> ancestor.
    ///
    /// The UI gets this for free from the scissor stack; a composited video visual does not, because it lives outside
    /// the UI back buffer entirely. Walking the parent chain is cheap — it runs once per coalesced pump, and the chain
    /// is a handful of nodes deep.
    ///
    /// E1: a <see cref="SizeMode.Reveal"/> ancestor animates <c>NodePaint.PresentedW</c>/<c>PresentedH</c>
    /// (<c>Columns.cs:110-113</c>) while <see cref="SceneStore.AbsoluteRect"/> keeps returning the node's FINAL
    /// laid-out bounds (<c>SceneStore.cs:1809-1824</c> never reads Presented*). Left alone, a revealing clipping
    /// ancestor narrows (or widens) what the UI actually paints/clips while this walk kept intersecting against the
    /// final rect — so the pumped video viewport disagreed with the visible clip and the composited frame spilled
    /// outside it. Mirror the exact sentinel the recorder itself uses at the same site
    /// (<c>SceneRecorder.cs:1185-1186</c>, also <c>SceneStore.cs:516-517</c>): not-NaN means a Reveal row is live and
    /// its presented value wins.</summary>
    private static RectF ClipToAncestors(SceneStore scene, NodeHandle node, RectF rect)
    {
        for (NodeHandle p = scene.Parent(node); !p.IsNull && scene.IsLive(p); p = scene.Parent(p))
        {
            if ((scene.Flags(p) & NodeFlags.ClipsToBounds) == 0) continue;
            RectF c = scene.AbsoluteRect(p);
            ref NodePaint pp = ref scene.Paint(p);
            float cw = float.IsNaN(pp.PresentedW) ? c.W : pp.PresentedW;
            float ch = float.IsNaN(pp.PresentedH) ? c.H : pp.PresentedH;
            c = new RectF(c.X, c.Y, cw, ch);
            float x0 = MathF.Max(rect.X, c.X), y0 = MathF.Max(rect.Y, c.Y);
            float x1 = MathF.Min(rect.X + rect.W, c.X + c.W), y1 = MathF.Min(rect.Y + rect.H, c.Y + c.H);
            rect = new RectF(x0, y0, MathF.Max(0f, x1 - x0), MathF.Max(0f, y1 - y0));
            if (rect.W <= 0f || rect.H <= 0f) return rect;
        }
        return rect;
    }


    // ── default transport (pure FluentGpu: two rows — a full-width seek row above a control row) ─────────────────────
    //
    // The two-row shape is WinUI MediaTransportControls' own recommendation and it is not negotiable here: NOTHING is
    // interleaved into the seek row. A control sitting beside the rail steals horizontal travel from the one gesture
    // whose precision matters most, and it puts a click target inside the strip the user sweeps blind.
    //
    // Left cluster:  play/pause · -10 s · +10 s · volume (icon + slider on hover) · elapsed / total
    // Right cluster: speed chip · quality chip · captions chip · audio track · PiP · ⋯ · FULLSCREEN LAST
    // Fullscreen sits at the extreme corner deliberately: in fullscreen that corner is a SCREEN corner, which is an
    // infinite-width Fitts target — the user can throw the pointer at it without aiming.
    private Element BuildTransport(float areaWidth, bool interactive, MediaSeekBar seekBar,
        Signal<float> chromeHeight, Signal<bool> volumeExpanded, Action toggleFullscreen,
        Ref<NodeHandle> ccAnchor, Ref<NodeHandle> qualityAnchor, Ref<NodeHandle> rateAnchor, Ref<NodeHandle> audioAnchor,
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
        _ = Player.Tracks.Video.Version.Value;
        _ = Player.Tracks.Text.Version.Value;
        _ = Player.Qualities.Variants.Version.Value;
        MediaTrack? text = Player.Tracks.SelectedText.Value;
        MediaTrack? audio = Player.Tracks.SelectedAudio.Value;
        QualitySelection quality = Player.Qualities.Selected.Value;
        QualityVariant? activeQuality = Player.Qualities.Active.Value;
        float rate = Player.Rate.Value;
        bool hasDuration = Player.Duration.Value > TimeSpan.Zero;
        bool volumeOpen = volumeExpanded.Value;

        // Width-dependent decisions wait for a MEASURE. On the first layout pass areaWidth is 0, so treating "unknown"
        // as "wide" rendered all three chips and then deleted them one frame later — the flash the compaction rule was
        // supposed to prevent. Unknown now means "not yet", for the time label and the chips alike.
        bool measured = areaWidth > 0f;
        bool compact = IsCompactTransport(areaWidth);
        bool presentingFullscreen = PresentingFullscreen;

        var playPause = IconButton(playIntent ? Icons.Pause : Icons.Play, () =>
        {
            if (playIntent) RequestPause(); else RequestPlay();
        }, interactive);

        var back10 = IconButton(Icons.Back, () => seekBar.SeekBy(-10f), interactive);
        var fwd10 = IconButton(Icons.Forward, () => seekBar.SeekBy(10f), interactive);

        // Volume: an icon that is a real mute toggle, plus a slider that expands on hover/focus. A bare mute toggle with
        // no slider is the one control users cannot substitute with anything else on the surface.
        var volumeChildren = new System.Collections.Generic.List<Element>(2)
        {
            IconButton(muted || Player.Volume.Peek() <= 0f ? Icons.Mute : Icons.Volume,
                () => Player.SetMuted(!muted), interactive),
        };
        if (volumeOpen && interactive)
            volumeChildren.Add(new BoxEl
            {
                Width = 84f, AlignItems = FlexAlign.Center,
                Children = [Slider.Create(Player.Volume, v => Player.SetVolume(v), length: 84f, thickness: 24f)],
            });
        var volume = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            OnPointerMoveWithin = _ => { volumeExpanded.Value = true; OnChromePointerMove(default); },
            OnPointerExit = () => volumeExpanded.Value = false,
            Children = volumeChildren.ToArray(),
        };

        var controls = new System.Collections.Generic.List<Element>(14) { playPause, back10, fwd10, volume };
        if (measured && areaWidth >= CompactTransportWidth)
            controls.Add(Embed.Comp(() => new MediaTransportTime { Player = Player, ScrubTargetSeconds = seekBar.ScrubTargetSeconds }));
        controls.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        // A live source says so on the surface, next to the control that acts on it. The chip is a STATUS READOUT, not a
        // button: it is what answers "is this stream live or a recording" while the transport is showing no duration and
        // no seek bar at all — the state the user would otherwise have to infer from an absence. The Go-Live button
        // beside it stays the only interactive element (and reads "● LIVE" once the playhead is at the edge, so the two
        // never claim different things: the chip states the SOURCE is live, the button states where the PLAYHEAD is).
        if (timeline.IsLive)
            controls.Add(LiveChip());
        if ((commands & MediaCommandFlags.GoLive) != 0)
            controls.Add(TextButton(timeline.IsAtLiveEdge ? MediaStrings.LiveEdge : MediaStrings.GoLive,
                () => _ = Player.GoLiveAsync(), interactive, timeline.IsAtLiveEdge));
        // Each chip renders its CURRENT VALUE, never a category label: "1×" not "Speed", "1080p" not "Quality",
        // "CC EN" not "Subtitles". A chip that names its category makes the user open it to find out what it is set to.
        // Each opens a PICKER flyout at its own anchor (never blind-cycles through the options).
        if (measured && !compact && (commands & MediaCommandFlags.Rate) != 0)
            controls.Add(TextButton(MediaStrings.RateLabel(rate), () => OpenPicker(rateAnchor, SpeedItems()), interactive)
                with { OnRealized = h => rateAnchor.Value = h });
        // The quality chip SURVIVES compaction. It is the readout for the "why am I watching 240p" question, and the
        // one chip whose value the user cannot infer from anything else on screen.
        if (measured && (commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
            controls.Add(TextButton(QualityLabel(quality, activeQuality), () => OpenPicker(qualityAnchor, QualityItems()), interactive)
                with { OnRealized = h => qualityAnchor.Value = h });
        if (measured && !compact && (commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
            controls.Add(TextButton(text is null ? MediaStrings.CaptionsShort : MediaStrings.CaptionsFor(text.Language ?? text.Label),
                () => OpenPicker(ccAnchor, CaptionItems()), interactive, text is not null)
                with { OnRealized = h => ccAnchor.Value = h });
        if (measured && !compact && (commands & MediaCommandFlags.SelectAudioTrack) != 0 && Player.Tracks.Audio.Count > 1)
            controls.Add(TextButton(audio?.Language ?? audio?.Label ?? MediaStrings.AudioTrack,
                () => OpenPicker(audioAnchor, AudioItems()), interactive)
                with { OnRealized = h => audioAnchor.Value = h });
        if (PictureInPictureRequested is { } pip && (commands & MediaCommandFlags.PictureInPicture) != 0)
            controls.Add(IconButton(Icons.OpenInNewWindow, pip, interactive));
        // The button raises the SAME context request as right-click / long-press / Menu-key. ContextMenu.Attach reads
        // args.Source at invoke time, so placement follows this live button without a captured realization handle.
        controls.Add(IconButton(Icons.More, static () => { }, interactive) with { OnClick = null, ClickRequestsContext = interactive });
        // LAST, at the extreme corner. See the cluster note above.
        controls.Add(IconButton(presentingFullscreen ? Icons.BackToWindow : Icons.FullScreen, toggleFullscreen, interactive));

        var rows = new System.Collections.Generic.List<Element>(2);
        // Never render a seek bar without a SCALE: a rail with nothing to map onto is a control that lies about what it
        // does. For ordinary media the scale is the duration. A live source has none — it is unbounded — but a DVR
        // window wide enough to aim inside (TimelineInfo.HasDvrWindow, the same 30 s threshold the backend uses to
        // decide whether to offer Seek at all) IS a scale, and MediaSeekBar maps the rail onto that window with the
        // live edge at its right end. Without this arm a rewindable live stream showed no rail at all.
        if (hasDuration || timeline.HasDvrWindow)
            rows.Add(new BoxEl
            {
                Key = "media-seek-row",
                Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Children = [Embed.Comp(() => seekBar)],
            });
        rows.Add(new BoxEl
        {
            Key = "media-control-row",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Children = controls.ToArray(),
        });

        return new BoxEl
        {
            Direction = 1,
            Gap = 2f,
            Padding = new Edges4(14, 34, 14, 8),
            // The canonical media footer scrim (Tok.ScrimBottom): controls sit on darkness that dissolves into the
            // video, the YouTube/Netflix-style overlay read.
            Gradient = Tok.ScrimBottom,
            // S3 — pointer over the CONTROL PANEL, not merely over the element. Resting the pointer on the play button
            // used to hide the bar out from under it (and the cursor with it), because the only tracker was a
            // whole-element MOVE handler and a resting pointer emits no moves.
            OnPointerMoveWithin = OnChromePointerMove,
            OnPointerExit = OnChromePointerExit,
            OnBoundsChanged = b =>
            {
                float h = MathF.Round(b.H);
                if (h != chromeHeight.Peek()) chromeHeight.Value = h;   // captions lift by exactly this
            },
            Children = rows.ToArray(),
        };

        void OpenPicker(Ref<NodeHandle> anchor, System.Collections.Generic.List<MenuFlyoutItem> items)
        {
            var node = anchor.Value;
            var scene = Context.Scene;
            if (node.IsNull || scene is null || !scene.IsLive(node)) return;
            OverlayHandle? m = null;
            // The anchor is a THUNK, not a snapshot. A transport re-render recreates the button and the captured handle
            // goes dead; the pin then drops, the chrome hides, and OverlayHost's dead-anchor prune closes the picker
            // under the user's hand. The More button was fixed for exactly this — so is every picker now.
            m = overlayService.Open(() => anchor.Value,
                () => MenuFlyout.Build(items, () => m?.Close()), FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menuOpen = true;      // S6, immediately — the epoch effect confirms it and clears it on close
            Reevaluate();
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

        System.Collections.Generic.List<MenuFlyoutItem> AudioItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Audio.Count);
            for (int i = 0; i < Player.Tracks.Audio.Count; i++)
            {
                MediaTrack track = Player.Tracks.Audio[i];
                items.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? MediaStrings.AudioIndexed(i + 1), audio?.Id == track.Id,
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

        System.Collections.Generic.List<MenuFlyoutItem> SpeedItems() =>
        [
            Speed(0.5), Speed(0.75), Speed(1), Speed(1.25), Speed(1.5), Speed(2),
        ];

        MenuFlyoutItem Speed(double value) => MenuFlyoutItem.RadioItem(MediaStrings.RateLabel((float)value), Math.Abs(rate - value) < 0.01,
            () => Player.SetRate(value));
    }

    // ── keyboard (Task 2) ────────────────────────────────────────────────────────────────────────────────────────────
    // Two rules run through the whole map and they are the single most preventable anger class in a video UI
    // (Jellyfin #1079 / #2376 / #2058):
    //   1. A transport button must NEVER keep keyboard focus after a MOUSE activation, or the next Space re-activates
    //      the last-clicked button instead of toggling play. IconButton/TextButton therefore carry
    //      AllowFocusOnInteraction = false — a pointer press never moves focus onto them, so focus stays on the video
    //      surface where Space belongs. Tab still reaches every one of them.
    //   2. Every binding works identically windowed and fullscreen, and stays LIVE while the chrome is HIDDEN — the
    //      handler is on the player frame, which never unmounts, not on the chrome, which fades.
    private const int VkComma = 188, VkPeriod = 190, VkOpenBracket = 219, VkCloseBracket = 221, VkSlash = 191;

    private void HandleKeyCore(KeyEventArgs e, MediaSeekBar seekBar, Action toggleFullscreen, Action exitFullscreenOnly,
        Signal<bool> shortcutsOpen)
    {
        bool shift = (e.Mods & KeyModifiers.Shift) != 0;
        if ((e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0) return;   // leave app chords alone

        switch (e.KeyCode)
        {
            case Keys.Space:
            case Keys.K:
                TogglePlay(); e.Handled = true; break;
            case Keys.Left:
                seekBar.SeekBy(shift ? -1f : -5f); e.Handled = true; break;
            case Keys.Right:
                seekBar.SeekBy(shift ? 1f : 5f); e.Handled = true; break;
            case Keys.J:
                seekBar.SeekBy(-10f); e.Handled = true; break;
            case Keys.L:
                seekBar.SeekBy(10f); e.Handled = true; break;
            case Keys.Up:
                AdjustVolume(VolumeStep); e.Handled = true; break;
            case Keys.Down:
                AdjustVolume(-VolumeStep); e.Handled = true; break;
            case Keys.M:
                Player.SetMuted(!Player.Muted.Peek()); e.Handled = true; break;
            case >= Keys.D0 and <= Keys.D9:
                JumpToPercent((e.KeyCode - Keys.D0) * 10); e.Handled = true; break;
            case VkComma:
                StepFrame(-1); e.Handled = true; break;
            case VkPeriod:
                StepFrame(1); e.Handled = true; break;
            case VkOpenBracket:
                ScaleRate(0.9f); e.Handled = true; break;
            case VkCloseBracket:
                ScaleRate(1f / 0.9f); e.Handled = true; break;
            case Keys.Back:
                Player.SetRate(1.0); e.Handled = true; break;
            case Keys.C:
                CycleCaptions(); e.Handled = true; break;
            case Keys.F:
            case Keys.F11:
                toggleFullscreen(); e.Handled = true; break;
            case VkSlash when shift:
                shortcutsOpen.Value = !shortcutsOpen.Peek(); e.Handled = true; break;
            // Escape EXITS fullscreen and does nothing else. It never enters it, and it never falls through to a
            // window-close: with IsHostFullscreen set it leaves through the host delegate, exactly as F11 does.
            case Keys.Escape when PresentingFullscreen:
                exitFullscreenOnly(); e.Handled = true; break;
            case Keys.Escape when shortcutsOpen.Peek():
                shortcutsOpen.Value = false; e.Handled = true; break;
            default:
                return;   // unhandled: no reveal, no re-arm — an unrelated key is not activity on this surface
        }
        ShowChrome();
        ShowCursor();
        Reevaluate();
    }

    private void TogglePlay()
    {
        if (Player.IsPlayRequested.Peek()) RequestPause(); else RequestPlay();
    }

    private void JumpToPercent(int percent)
    {
        var dur = Player.Duration.Peek();
        if (dur <= TimeSpan.Zero) return;
        RequestSeekVia(TimeSpan.FromSeconds(dur.TotalSeconds * (percent / 100.0)));
    }

    private void RequestSeekVia(TimeSpan target)
    {
        if (_seekBar is { } bar) bar.SeekTo(target);       // keeps the confirm-gated hold + the target readout
        else RequestSeek(target, SeekMode.Accurate);
    }

    /// <summary>Frame step with AUTO-PAUSE: stepping while playing is meaningless, so the step implies the pause the
    /// user was about to press anyway.</summary>
    private void StepFrame(int delta)
    {
        if (Player.IsPlayRequested.Peek()) RequestPause();
        _ = Player.StepFrame(delta);
    }

    private void ScaleRate(float factor)
        => Player.SetRate(Math.Clamp(Player.Rate.Peek() * factor, MinRate, MaxRate));

    private const float MinRate = 0.25f, MaxRate = 2.0f;

    /// <summary>C cycles Off → track 1 → … → Off. A cycle (not a picker) is right for a KEY: the user is not looking at
    /// the screen when they press it, and the menu is one right-click away when they are.</summary>
    private void CycleCaptions()
    {
        var tracks = Player.Tracks.Text;
        if (tracks.Count == 0) return;
        MediaTrack? current = Player.Tracks.SelectedText.Peek();
        int index = -1;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Id == current?.Id) { index = i; break; }
        int next = index + 1;
        _ = Player.SelectTrackAsync(next >= tracks.Count ? null : tracks[next]);
    }


    private void RequestPlay()
    {
        if (PlayRequested is { } request) request();
        else _ = Player.PlayAsync();
    }

    private void RequestPause()
    {
        if (PauseRequested is { } request) request();
        else _ = Player.PauseAsync();
    }

    private void RequestSeek(TimeSpan target, SeekMode mode)
    {
        if (SeekRequested is { } request) request(target, mode);
        else _ = Player.SeekAsync(target, mode);
    }

    /// <summary>Build the complete More/context menu against LIVE state. This is invoked once per open by
    /// <see cref="ContextMenu.Attach"/>; no render-time snapshot or mount-frozen host row can stale its radio marks.</summary>
    private System.Collections.Generic.List<MenuFlyoutItem> BuildMoreMenuItems(
        Signal<VideoAspectMode> aspectSignal, Signal<double> customAspectSignal,
        Action<VideoAspectMode, double> setAspect, Action toggleFullscreen)
    {
        VideoAspectMode aspect = aspectSignal.Peek();
        double customAspect = customAspectSignal.Peek();
        MediaCommandFlags commands = Player.Commands.Available.Peek();
        MediaTrack? text = Player.Tracks.SelectedText.Peek();
        QualitySelection quality = Player.Qualities.Selected.Peek();
        float rate = Player.Rate.Peek();

        var items = new System.Collections.Generic.List<MenuFlyoutItem>(12);
        IReadOnlyList<MenuFlyoutItem>? hostItems = MoreMenuItems?.Invoke();
        if (hostItems is { Count: > 0 })
        {
            for (int i = 0; i < hostItems.Count; i++) items.Add(hostItems[i]);
            items.Add(MenuFlyoutItem.Separator);
        }
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
        if ((commands & MediaCommandFlags.SelectVideoTrack) != 0 && Player.Tracks.Video.Count > 1)
            items.Add(MenuFlyoutItem.SubMenu(MediaStrings.VideoTrack, VideoItems()));
        if ((commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
            items.Add(MenuFlyoutItem.SubMenu(MediaStrings.Captions, CaptionItems()));
        if ((commands & MediaCommandFlags.Chapters) != 0)
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new MenuFlyoutItem(MediaStrings.PreviousChapter, Icons.Previous, Invoke: () => _ = Player.PreviousChapterAsync()));
            items.Add(new MenuFlyoutItem(MediaStrings.NextChapter, Icons.Next, Invoke: () => _ = Player.NextChapterAsync()));
        }
        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(PresentingFullscreen ? MediaStrings.ExitFullscreen : MediaStrings.Fullscreen,
            PresentingFullscreen ? Icons.BackToWindow : Icons.FullScreen, Invoke: toggleFullscreen) { AcceleratorText = MediaStrings.F11 });
        return items;

        System.Collections.Generic.List<MenuFlyoutItem> CaptionItems()
        {
            var result = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Text.Count + 1)
            { MenuFlyoutItem.RadioItem(MediaStrings.Off, text is null, () => _ = Player.SelectTrackAsync(null)) };
            for (int i = 0; i < Player.Tracks.Text.Count; i++)
            {
                MediaTrack track = Player.Tracks.Text[i];
                result.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? MediaStrings.CaptionsIndexed(i + 1), text?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return result;
        }

        System.Collections.Generic.List<MenuFlyoutItem> QualityItems()
        {
            var result = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Qualities.Variants.Count + 1)
            { MenuFlyoutItem.RadioItem(MediaStrings.Auto, quality.IsAuto, () => _ = Player.SelectQualityAsync(QualitySelection.Auto)) };
            for (int i = 0; i < Player.Qualities.Variants.Count; i++)
            {
                QualityVariant variant = Player.Qualities.Variants[i];
                string id = variant.Id;
                string label = variant.Resolution.Height > 0 ? MediaStrings.QualityHeight(variant.Resolution.Height) : variant.Label ?? id;
                result.Add(MenuFlyoutItem.RadioItem(label, !quality.IsAuto && quality.VariantId == id,
                    () => _ = Player.SelectQualityAsync(QualitySelection.Pin(id))));
            }
            return result;
        }

        System.Collections.Generic.List<MenuFlyoutItem> AudioItems()
        {
            var result = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Audio.Count);
            MediaTrack? audio = Player.Tracks.SelectedAudio.Peek();
            for (int i = 0; i < Player.Tracks.Audio.Count; i++)
            {
                MediaTrack track = Player.Tracks.Audio[i];
                result.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? MediaStrings.AudioIndexed(i + 1), audio?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return result;
        }

        System.Collections.Generic.List<MenuFlyoutItem> VideoItems()
        {
            var result = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Video.Count);
            MediaTrack? video = Player.Tracks.SelectedVideo.Peek();
            for (int i = 0; i < Player.Tracks.Video.Count; i++)
            {
                MediaTrack track = Player.Tracks.Video[i];
                result.Add(MenuFlyoutItem.RadioItem(track.Label, video?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return result;
        }

        System.Collections.Generic.List<MenuFlyoutItem> SpeedItems() =>
        [
            Speed(0.5), Speed(0.75), Speed(1), Speed(1.25), Speed(1.5), Speed(2),
        ];

        MenuFlyoutItem Speed(double value) => MenuFlyoutItem.RadioItem(MediaStrings.RateLabel((float)value), Math.Abs(rate - value) < 0.01,
            () => Player.SetRate(value));
    }

    /// <summary>The <c>elapsed / total</c> time label as its OWN component. It bridges the ~per-frame position/duration
    /// signals into WHOLE-SECOND value-gated signals via eager effects, so it re-renders at most ~once per second (never
    /// per frame) — the position tick reaches the compositor-bound seek bar, not this text.</summary>
    private sealed class MediaTransportTime : Component
    {
        public required IMediaPlayer Player { get; init; }
        /// <summary>The seek bar's live scrub target (negative = none). While a scrub is in flight this label shows
        /// where the user is GOING within 100 ms of pointer-down, regardless of decode — the decoded position may not
        /// move for seconds on the DRM path, and a readout that sits still under a moving thumb reads as a freeze.</summary>
        public IReadSignal<float>? ScrubTargetSeconds { get; init; }
        private readonly Signal<int> _posSec = new(0);
        private readonly Signal<int> _durSec = new(-1);
        /// <summary>Where the rail's scale STARTS, in seconds. Zero for ordinary media; the DVR window's start for a
        /// live source, whose positions are absolute media times inside a window that does not begin at zero.</summary>
        private readonly Signal<int> _originSec = new(0);

        public override Element Render()
        {
            UseSignalEffect(() =>
            {
                float target = ScrubTargetSeconds?.Value ?? -1f;
                int s = target >= 0f ? (int)target : (int)Player.PositionSeconds.Value;
                if (s != _posSec.Peek()) _posSec.Value = s;
            });
            UseSignalEffect(() =>
            {
                var d = Player.Duration.Value;
                TimelineInfo timeline = Player.Timeline.Value;
                // A live source publishes NO duration, so "x / 0:00" is all this label could say — while the rail
                // beside it is happily mapped to the DVR window. Read the same scale the rail does: elapsed WITHIN the
                // window over the window's length. Both are whole seconds, so the sliding window (10 Hz) moves these
                // signals at most once a second, which is the cadence this label already re-rendered at.
                bool dvr = d <= TimeSpan.Zero && timeline.HasDvrWindow;
                int s = d > TimeSpan.Zero ? (int)d.TotalSeconds : dvr ? (int)timeline.DvrWindow.TotalSeconds : -1;
                int origin = dvr ? (int)timeline.SeekableStart.TotalSeconds : 0;
                if (s != _durSec.Peek()) _durSec.Value = s;
                if (origin != _originSec.Peek()) _originSec.Value = origin;
            });
            int pos = _posSec.Value - _originSec.Value;
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
        public Action? PlayRequested { get; init; }
        public Action? PauseRequested { get; init; }
        public Action<TimeSpan, SeekMode>? SeekRequested { get; init; }
        public required VideoBinding Binding { get; init; }
        public required Signal<VideoAspectMode> AspectMode { get; init; }
        public required Signal<double> CustomAspectRatio { get; init; }
        public required Signal<bool> FullscreenState { get; init; }
        public required Action Exit { get; init; }
        public Action<VideoAspectMode, double>? AspectModeChanged { get; init; }
        public ColorF LetterboxColor { get; init; }
        public CursorAutoHidePolicy CursorAutoHide { get; init; } = CursorAutoHidePolicy.FullscreenOnly;

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
                        PlayRequested = PlayRequested,
                        PauseRequested = PauseRequested,
                        SeekRequested = SeekRequested,
                        PresentationBinding = Binding,
                        AspectMode = AspectMode,
                        CustomAspectRatio = CustomAspectRatio,
                        AspectModeChanged = AspectModeChanged,
                        LetterboxColor = LetterboxColor,
                        FullscreenState = FullscreenState,
                        IsFullscreenPresentation = true,
                        ExitFullscreen = Exit,
                        CursorAutoHide = CursorAutoHide,
                    }),
                ],
            };
        }
    }

    // Hidden chrome is NOT merely transparent: `interactive` strips the Role (so it leaves the accessibility surface),
    // forces TabStop off (so it leaves the tab order) and disables the node (so it takes no activation) — on top of the
    // chrome root's HitTestVisible = false, which removes the whole subtree from hit-testing. A control panel that is
    // invisible but still hittable/focusable eats clicks meant for the video and traps Tab in nothing.
    //
    // AllowFocusOnInteraction = false is the fix for the Jellyfin class of bug: a MOUSE press never moves focus onto a
    // transport button, so focus stays on the video surface and the next Space toggles play instead of re-activating
    // the last-clicked button. Tab still reaches every button (keyboard focus is unaffected).
    private BoxEl IconButton(string glyph, Action onClick, bool interactive) => new()
    {
        Width = 40f, Height = 40f, Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Tok.OnMediaPrimary with { A = 0.09f }, PressedFill = Tok.OnMediaPrimary with { A = 0.16f },
        Role = interactive ? AutomationRole.Button : default,
        TabStop = interactive ? null : false,
        IsEnabled = interactive,
        AllowFocusOnInteraction = false,
        OnClick = onClick,
        OnPointerPressed = OnChromePointerPressed,
        OnFocusChanged = OnChromeFocusChanged,
        Children = new Element[] { new TextEl(glyph) { Size = 16f, Color = Tok.OnMediaPrimary, FontFamily = Theme.IconFont } },
    };

    /// <summary>The LIVE status chip. Deliberately NOT a button: no Role, no TabStop, no hover/press fill and no click —
    /// a chip that looks pressable but does nothing is worse than no chip. It carries the critical-fill dot the whole
    /// system uses for "on air" plus the word, because the dot alone is not a label and color alone is not an
    /// accessible signal.</summary>
    private BoxEl LiveChip() => new()
    {
        Height = 34f, Padding = new Edges4(8f, 0f, 8f, 0f), Corners = Radii.ControlAll,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 6f,
        Children =
        [
            new BoxEl { Width = 6f, Height = 6f, Corners = Radii.Circle(6f), Fill = Tok.SystemFillCritical },
            new TextEl(MediaStrings.Live) { Size = 12f, Color = Tok.OnMediaPrimary },
        ],
    };

    private BoxEl TextButton(string label, Action onClick, bool interactive, bool active = false) => new()
    {
        Height = 34f, MinWidth = 42f, Padding = new Edges4(8f, 0f, 8f, 0f), Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = active ? Tok.AccentDefault : ColorF.Transparent,
        HoverFill = active ? Tok.AccentSecondary : Tok.OnMediaPrimary with { A = 0.09f },
        PressedFill = active ? Tok.AccentTertiary : Tok.OnMediaPrimary with { A = 0.16f },
        Role = interactive ? AutomationRole.Button : default,
        TabStop = interactive ? null : false,
        IsEnabled = interactive,
        AllowFocusOnInteraction = false,
        OnClick = onClick,
        OnPointerPressed = OnChromePointerPressed,
        OnFocusChanged = OnChromeFocusChanged,
        Children = [new TextEl(label) { Size = 12f, Color = Tok.OnMediaPrimary }],
    };

    private string QualityLabel(QualitySelection selection, QualityVariant? active)
    {
        // "240p (Auto)", never "Auto · 240p". The NUMBER is what the user is looking for — it is the answer to "why
        // does this look like that" — and the mode is the parenthetical. Putting Auto first buries the readout behind a
        // word that is the same on every stream, which is precisely how a silent ABR downshift goes unnoticed. An
        // UPSHIFT changes this label exactly as visibly as a downshift does; the label follows Qualities.Active, so it
        // moves on the acknowledged rung and not on the request.
        if (selection.IsAuto)
            return active is { Resolution.Height: > 0 }
                ? MediaStrings.QualityHeight(active.Resolution.Height) + " (" + MediaStrings.Auto + ")"   // loc-allow: parenthesis punctuation around two localized parts
                : MediaStrings.Auto;
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

    /// <summary>The startup cue. It is ANIMATED and indeterminate by default — a static glyph is indistinguishable
    /// from a hang, which is the one thing a loading state must never be. A DETERMINATE readout appears only once the
    /// start has been slow enough (10 s) for a number to be information rather than noise, and only when the backend
    /// actually knows one (<paramref name="percent"/> in 0..1).</summary>
    private static Element OpeningOverlay(bool playIntent, double percent) => new BoxEl
    {
        // No card, no border: a quiet centered spinner over the poster (the WinUI/streaming-player loading read).
        Direction = 1,
        Gap = 14f,
        AlignItems = FlexAlign.Center,
        Children =
        [
            percent is >= 0 and <= 1 ? ProgressRing.Determinate((float)percent, 40f) : ProgressRing.Indeterminate(40f),
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

    /// <summary>UNCONDITIONAL force-show: no play intent at all, or a TERMINAL failure. Deliberately NOT "anything that
    /// is not Playing" any more. The old predicate treated every non-Playing state as a force-show, and the protected
    /// session maps both Licensed and Buffering onto <see cref="PlaybackState.Buffering"/> while sampling native state
    /// only every 250 ms — so one rebuffer blip re-pinned the chrome and a hung start pinned it forever. A transient
    /// buffer is S2 (bounded, see <c>BufferingSuppressMaxMs</c>) and a slow open is the S1 grace
    /// (<c>OpeningForceShowMs</c>); neither belongs here.</summary>
    internal static bool ShouldForceChrome(bool playIntent, PlaybackState state)
        => !playIntent || state == PlaybackState.Failed;

    /// <summary>S1 — a REAL, user-visible stop: paused, idle, ended, or opened-but-not-yet-started. A transient
    /// buffer/stall is NOT a stop (that is S2) and must not be confused for one.</summary>
    internal static bool IsStoppedState(PlaybackState state)
        => state is PlaybackState.Idle or PlaybackState.Ready or PlaybackState.Paused or PlaybackState.Ended;

    /// <summary>Below <c>CompactTransportWidth</c> the chips fold into the ⋯ menu. Unknown width (0, the first layout
    /// pass) is NOT compact and NOT wide — callers wait for a measure rather than rendering chips that vanish.</summary>
    internal static bool IsCompactTransport(float width) => width > 0f && width < CompactTransportWidth;

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

    /// <summary>The "?" reference sheet. It is a light-dismiss LAYER inside the player, not a flyout: the bindings it
    /// documents must stay live while it is up (pressing Space with the sheet open should still toggle play), and a
    /// focus-trapping popup would swallow exactly the keys the sheet is teaching.
    ///
    /// <para>The action labels are deliberate literals: the control kit's loc table (<c>assets/loc/en-US.json</c>) has
    /// no keys for a shortcut sheet yet, and inventing <c>Strings.Media.*</c> members that resolve to nothing would
    /// render an empty sheet. They are marked so the extraction pass can find them in one grep.</para></summary>
    private static Element ShortcutOverlay(Action close)
    {
        var rows = new System.Collections.Generic.List<Element>(16);
        Row("Space  K", "Play / pause");           // loc-allow: shortcut sheet — kit loc keys pending
        Row("← →", "Seek 5 s");                    // loc-allow: shortcut sheet — kit loc keys pending
        Row("J  L", "Seek 10 s");                  // loc-allow: shortcut sheet — kit loc keys pending
        Row("Shift ← →", "Seek 1 s");              // loc-allow: shortcut sheet — kit loc keys pending
        Row("↑ ↓", "Volume");                      // loc-allow: shortcut sheet — kit loc keys pending
        Row("M", "Mute");                          // loc-allow: shortcut sheet — kit loc keys pending
        Row("0 – 9", "Jump to 0-90 %");            // loc-allow: shortcut sheet — kit loc keys pending
        Row(",  .", "Frame step");                 // loc-allow: shortcut sheet — kit loc keys pending
        Row("[  ]", "Speed");                      // loc-allow: shortcut sheet — kit loc keys pending
        Row("F  F11", MediaStrings.Fullscreen);
        Row("Esc", MediaStrings.ExitFullscreen);
        Row("C", MediaStrings.Captions);

        return new BoxEl
        {
            Key = "media-shortcuts",
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = Tok.MediaScrim,
            OnClick = close,
            Animate = LoadingMotion,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = 6f,
                    Padding = new Edges4(22f, 20f, 22f, 20f),
                    Corners = Radii.OverlayAll,
                    Fill = Tok.MediaScrim,
                    HitTestVisible = false,
                    Children = rows.ToArray(),
                },
            ],
        };

        void Row(string keys, string action) => rows.Add(new BoxEl
        {
            Direction = 0, Gap = 16f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = 110f, Children = [new TextEl(keys) { Size = 12f, Color = Tok.OnMediaSecondary }] },
                new TextEl(action) { Size = 12f, Color = Tok.OnMediaPrimary },
            ],
        });
    }

    private static Element CaptionOverlay(TimedCue cue, float bottomMargin) => new BoxEl
    {
        Key = "media-caption",
        AlignSelf = FlexAlign.Center,
        MaxWidth = 880f,
        Animate = CaptionMotion,
        Margin = new Edges4(24f, 0f, 24f, bottomMargin),
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
    public static string Live => Loc.Get(Strings.Media.Live);
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
    public static string VideoTrack => Loc.Get(Strings.Media.VideoTrack);
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
