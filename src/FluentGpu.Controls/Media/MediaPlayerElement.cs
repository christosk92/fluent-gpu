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
/// <item><b>One auto-hide policy.</b> Show/hide is a PURE clocked state machine (<see cref="PlayerChromeVisibility"/>,
///   unit-tested): user ACTIVITY is the only thing that reveals (entering, a move off the rest point, a press, the wheel,
///   a handled key, keyboard focus, a user-visible stop); HOLDS (hovering the controls, a menu, a scrub, keyboard focus,
///   a pause) only keep visible chrome up; buffering and opening do neither. Every input feeds it and then calls ONE
///   chokepoint (<c>Sync</c>) that publishes the visibility signal and keeps exactly one wake armed — which retires both
///   the "flyout closed but the timer never restarted" and the "controls pop up by themselves" bug classes. The chrome
///   stays MOUNTED across the cycle (a stable Key): visibility rides an opacity fade seeded from the LIVE value plus the
///   hit-test and focusability channel, so hidden chrome is out of the hit-test, focus and accessibility trees and a
///   re-reveal never rebuilds the seek bar at width 0.</item>
/// <item><b>Controlled inputs + tokens.</b> Aspect/fullscreen are concrete signals (auto-materialized when absent); all
///   on-media ink/scrim/stage reads a <c>Tok.*</c> media token — no hardcoded colors.</item>
/// <item><b>No remount on a source switch.</b> The video stage's child shape ("media-stage"/"media-hole"/"media-poster")
///   is FIXED for the element's whole life — never conditionally mounted/unmounted on readiness. The hole's
///   <c>VideoHole</c> flag and the poster's opacity are PROPS that toggle on the same nodes; a latch keeps the hole
///   active across a switch (Playing → Opening on the same element) so the next source's first frame arrives by
///   crossfade under the poster instead of a subtree rebuild — see <see cref="Render"/>'s video-stage comment.</item>
/// </list>
/// The default transport is pure FluentGpu (our own GPU text + a scrub <c>Slider</c>) — there is NO OS control to crash
/// on. TerraFX-free: references only Engine/Controls types + the <see cref="IMediaPlayer"/>/<see cref="VideoBinding"/> seam.
/// </summary>
public sealed class MediaPlayerElement : Component
{
    /// <summary>The poster cross-fades in/out as readiness flips — 150 ms, the shortest fade that still reads as a
    /// hand-off rather than a cut.</summary>
    private const float PosterCrossFadeMs = 150f;
    /// <summary>The poster's hand-off token: a straight opacity cross-fade, no scale, no slide — anything more is a
    /// transition ON TOP of a transition when the picture is already changing. <see cref="ReducedMotionPolicy.KeepFade"/>:
    /// this fade AIDS orientation (it is what tells the user whether they are looking at the poster or the live frame),
    /// so — like <see cref="MotionTok"/>'s own docs for the policy — it keeps running under reduced motion rather than
    /// snapping; only structural transforms snap. Seeded via <see cref="AnimEngine.SeedEased"/> (the same idiom the
    /// chrome fade below uses), so <see cref="MotionTokenDef.EffectiveDurationMs"/> resolves reduced-motion as a VALUE
    /// at the seed, never a branch in this authoring code.</summary>
    private static readonly MotionTokenDef PosterCrossFade =
        MotionTokenDef.Eased(PosterCrossFadeMs, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);
    /// <summary>Nothing at all is drawn over the poster for this long. A spinner that flashes for 200 ms is worse than
    /// no spinner: it reports trouble that did not happen.</summary>
    private const float StartupSpinnerDelayMs = 500f;
    /// <summary>Only past this may a DETERMINATE readout appear — before it, a percentage is noise with a number on it.</summary>
    private const float StartupDetailDelayMs = 10_000f;
    /// <summary>Volume nudge for the Up/Down keys and the wheel (5 points, the universal player step).</summary>
    private const float VolumeStep = 0.05f;
    /// <summary>Transport compaction threshold (DIP): below it, the chips fold into the ⋯ menu.</summary>
    private const float CompactTransportWidth = 420f;
    /// <summary>Hysteresis exit: once compact, the transport un-compacts only above this (see the compact derivation).</summary>
    private const float CompactTransportExitWidth = 460f;
    private bool _transportCompact;

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
        // The transport's More menu opens OVER the video by construction, and a video hole is a DestOut erase whose
        // backdrop is premultiplied zero — an acrylic plate there blurs nothing while still costing a backdrop pass every
        // frame the video under it changes. Flat from frame one, the same opt-out OpenPicker takes.
        OpaqueSurface = true,
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
        PresentingFullscreen ? default
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
    /// <summary>Draw the element's own status overlay (the opening spinner, the Seeking / Buffering / Changing quality
    /// pill, the failure card) over the picture. Default true. A host that owns its loading and failure visuals (Wavee's
    /// join poster stacks above the whole element, so a spinner mounted here would animate where nobody can see it, and
    /// its error line speaks for a failure) sets this false: the element then mounts NO status overlay at all, so no
    /// timer, ring or enter/exit tween runs for pixels the host covers. A decorative element never has one either.
    /// Frozen at mount, like every init prop.</summary>
    public bool ShowStatusOverlay { get; init; } = true;
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
    /// <summary>The idle machine's ONE output, published for a host that draws its own on-media chrome instead of this
    /// element's transport. Bind it rather than building a second timer: dwell, the leave debounce, the scrub / menu /
    /// keyboard / window-move holds and the accessibility override all live in one pure machine, and the cursor hides
    /// off the same edge — two machines would mean a host strip that is up while the cursor is gone. The element only
    /// ever writes it (from the timer sync, never from Render); a host only reads.</summary>
    public Signal<bool>? ChromeVisibleOut { get; init; }
    /// <summary>The activity seam for a host that draws its OWN on-media chrome instead of this element's transport
    /// (<see cref="SuppressTransport"/>). The host's controls are SIBLINGS of this element in the host's tree, so the
    /// dispatcher's routed pointer-within events (ancestors only) never see them — without this seam, hovering the
    /// host's own strip is invisible to <see cref="PlayerChromeVisibility"/> and the chrome fades under the pointer.
    /// An init prop, so the host hands in a STABLE instance (a field, never a render local) — see
    /// <see cref="PlayerChromeFeed"/>'s own doc. With <see cref="SuppressTransport"/> set this element never mounts its
    /// own seek bar or transport, so the machine's OverControls/Scrubbing/Pressed/WindowMove holds are otherwise never
    /// written at all; the feed becomes their ONLY writer, and the two paths can never disagree about a hold's state.</summary>
    public PlayerChromeFeed? ChromeFeed { get; init; }
    /// <summary>The host is presenting this element fullscreen (its own surface, not the element's overlay path).
    /// Drives the transport glyph, the ⋯ row label and Esc handling so a host-owned fullscreen does not render a
    /// control named for the state the user is already in. Distinct from the internal overlay-owned
    /// <see cref="IsFullscreenPresentation"/>; the two are OR-ed everywhere the presentation state is consulted.
    /// Frozen at mount — prefer <see cref="HostFullscreen"/> when the bit can change without remounting the hole.</summary>
    public bool IsHostFullscreen { get; init; }

    /// <summary>Live host-fullscreen bit. Read each render so a stay-mounted hole can fill the monitor without a
    /// generation remount. When set, OR-ed with <see cref="IsHostFullscreen"/> into <see cref="PresentingFullscreen"/>.</summary>
    public IReadSignal<bool>? HostFullscreen { get; init; }

    /// <summary>The element is being PRESENTED fullscreen, by either route (its own overlay, or a host surface that set
    /// <see cref="IsHostFullscreen"/>). Every label, glyph, Escape guard and cursor-policy decision reads this, never
    /// the overlay-only flag — a control named for the state the user is already in reads as a dead button.</summary>
    private bool PresentingFullscreen => IsFullscreenPresentation || HostPresenting;

    /// <summary>Host-owned fullscreen from EITHER route: the frozen <see cref="IsHostFullscreen"/> bit or the live
    /// <see cref="HostFullscreen"/> signal. Every exit decision reads this. Testing only the frozen bool left Escape a
    /// silent no-op for a host that drives fullscreen through the signal — which is exactly the shape a stay-mounted
    /// hole has, and the shape the signal exists to serve.</summary>
    private bool HostPresenting => IsHostFullscreen || (HostFullscreen?.Peek() ?? false);

    /// <summary>The ways OUT of a fullscreen presentation.</summary>
    internal enum FullscreenExit : byte { None, OverlayPresentation, Host, OwnOverlay }

    /// <summary>Which way out Escape takes. Escape only ever EXITS — never enters, never quits. The overlay
    /// presentation leaves through its own exit; a host-owned fullscreen leaves through the HOST, because the host put
    /// us there and only it knows how to put us back; the element's own overlay is torn down locally. Pure, so the
    /// three-way choice is a unit test rather than something you can only find by pressing Escape in the one placement
    /// that gets it wrong.</summary>
    internal static FullscreenExit ExitRouteFor(bool overlayPresentation, bool hostPresenting, bool ownOverlayOpen)
        => overlayPresentation ? FullscreenExit.OverlayPresentation
         : hostPresenting ? FullscreenExit.Host
         : ownOverlayOpen ? FullscreenExit.OwnOverlay
         : FullscreenExit.None;

    /// <summary>Host (or overlay) fullscreen drops the floor so a stretched host path can fill the monitor.</summary>
    internal static float VideoAreaMinHeight(bool presentingFullscreen, bool decorative)
        => presentingFullscreen || decorative ? 0f : 160f;

    /// <summary>The two modes whose fitted rect deliberately OVERFLOWS the area: crop (UniformToFill) scales up to
    /// cover, and native (true 1:1 device pixels) simply is whatever size the frame is — both rely on the viewport
    /// clip to crop the excess rather than a fit that shrinks to stay inside.</summary>
    internal static bool ModeMayOverflow(VideoAspectMode mode) => mode is VideoAspectMode.UniformToFill or VideoAspectMode.Native;
    /// <summary>Crop (UniformToFill) and native (1:1) keep an overflowing rect + viewport clip. The pump clamp would
    /// shrink that overflow back into the stage and silently behave like Fill/Uniform instead.</summary>
    internal static bool PumpClampsOverflow(VideoAspectMode mode) => !ModeMayOverflow(mode);
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
    (VideoAspectMode Mode, int Aw, int Ah, int Nw, int Nh, int Vw, int Vh, int Rw, int Rh, int Host, int Pres) _loggedPump;

    // Geometry motion (a drag, a resize, an animated placement) pumps only to PLACE the surface; the session publish, the
    // adaptive-viewport bookkeeping and the [video] pump line wait for the geometry to settle. One trailing timer, armed
    // once per motion burst, requests the FULL pump GeometrySettleMs after the last moved frame — it is a real timer, so
    // the final placement is published even when the host has gone idle (nothing else would ever pump again).
    private const float GeometrySettleMs = 200f;
    private TimerHandle _settle;
    private double _geomMotionMs;      // host timer clock of the last geometry-driven pump
    private bool _settleArmed;
    private readonly Action _onGeometrySettled;

    /// <summary>Create the control's stable delegates once: the UI-post drain reused by every native media event, and the
    /// chrome machine's handlers (the wake, the pointer, the window-move gesture) — so no input allocates a delegate.</summary>
    public MediaPlayerElement()
    {
        _drainPumpRequest = DrainPumpRequest;
        _onGeometrySettled = OnGeometrySettled;
        _resolveFocusOut = ResolveFocusOut;
        _onWake = OnWake;
        _onMoveSizeEnded = OnMoveSizeEnded;
        _onWindowBlur = OnWindowBlur;
        _seedPointerPresence = SeedPointerPresence;
        _onPointerMoved = OnPointerMoved;
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

    /// <summary>Record one geometry-driven pump and make sure the trailing settle timer is armed (once per burst — the
    /// per-frame cost is two stores, never a timer restart).</summary>
    private void NoteGeometryMotion()
    {
        _geomMotionMs = _settle.NowMs;
        if (_settleArmed) return;
        _settleArmed = true;
        _settle.RestartIn(GeometrySettleMs);
    }

    /// <summary>The settle timer fired: if the geometry kept moving, wait out the rest of the quiet window; otherwise the
    /// motion ended, so request the one FULL pump that publishes the settled placement (SurfaceGeometry, the adaptive
    /// viewport height, the content size for the final rect) and the settled [video] pump line.</summary>
    private void OnGeometrySettled()
    {
        if (!_settleArmed) return;     // also the harmless mount-time fire of the hook's initial arm
        double quiet = _settle.NowMs - _geomMotionMs;
        if (quiet < GeometrySettleMs - 1.0) { _settle.RestartIn((float)(GeometrySettleMs - quiet)); return; }
        _settleArmed = false;
        _binding.RequestPump();
    }

    // -----------------------------------------------------------------------------------------------------------
    //  The transport chrome's show/hide.
    //
    //  The POLICY is a pure clocked state machine (PlayerChromeVisibility — unit-tested, no scene/hooks/timers); this
    //  element only feeds it and applies its outputs. Every input (pointer, key, focus, menu, scrub, playback, the OS
    //  move loop) calls one machine method and then Sync(): tick at the host timer clock, publish the ONE visibility
    //  signal, apply the cursor override, keep exactly one wake armed at the machine's next deadline. There is no second
    //  path that shows, hides, arms or cancels anything — which retires both the "flyout closed but the timer never
    //  restarted" class and the "controls pop up by themselves" class (the old machine let every suppressor REVEAL: a
    //  rebuffer, an ABR quality switch or the Opening grace forced the chrome visible mid-playback).
    //
    //  These are FIELDS, not render locals: the timer callback and the element's stable event delegates must reach the
    //  same state without re-creating a closure per render, and most of the inputs (pointer, focus, menu) are not
    //  render-visible at all — writing them to a signal would re-render the player for a pointer move.
    // -----------------------------------------------------------------------------------------------------------

    private Signal<bool>? _chromeVisible;
    private InputHooks? _hooks;
    private Signal<bool>? _fullscreenState;
    private MediaSeekBar? _seekBar;
    private PlayerChromeVisibility? _vis;
    // The ONE wake. Its deadline is computed by the machine (TimerHandle.RestartIn); _armedDueMs is where it is armed
    // right now, so Sync re-arms only when the deadline moves EARLIER — see Sync.
    private TimerHandle _wake;
    private double _armedDueMs = double.PositiveInfinity;
    private bool _focusOutPending;
    // The device scale the most recent PumpNow ran at (1 until the first pump). Render-time FitVideoRect uses it so
    // the hole's layout margins agree with the pump's DComp placement in VideoAspectMode.Native (natural is px).
    private float _lastPumpScale = 1f;
    private bool _cursorHidden;
    private NodeHandle _playerRoot;
    private readonly Action _resolveFocusOut, _onWake, _onMoveSizeEnded, _onWindowBlur, _seedPointerPresence;
    private readonly Action<Point2> _onPointerMoved;
    private bool _pressIsTouch, _moveInFlight;
    // When the last OS move loop ended (host timer clock). A press within the double-click window of it is never the
    // second half of a double-click: a press right after a caption drag lands on the same client point, and it is not
    // a request for fullscreen.
    private double _lastWindowMoveMs = double.NegativeInfinity;

    /// <summary>The host timer clock (ms) — the SAME clock the wake is scheduled on, so the machine's deadlines and the
    /// timer agree (headless runs a virtual frame clock that <c>Environment.TickCount64</c> never sees).</summary>
    private double Now() => _wake.NowMs;

    /// <summary>THE chokepoint. Every input feeds the pure machine, then calls this: tick at the host timer clock →
    /// publish the ONE visibility signal (value-gated) → apply the cursor override → keep exactly one wake armed at the
    /// machine's next deadline. Idempotent, so handlers, effects and the wake itself may all call it.</summary>
    private void Sync()
    {
        if (_vis is not { } vis) return;
        double now = _wake.NowMs;
        vis.Tick(now);
        if (ChromeVisibleOut is { } outSig && outSig.Peek() != vis.ChromeVisible) outSig.Value = vis.ChromeVisible;
        if (_chromeVisible is { } sig && sig.Peek() != vis.ChromeVisible)
        {
            sig.Value = vis.ChromeVisible;
            // Always-on, one line per visibility EDGE (never per frame): the evidence trail for "why did the controls
            // show/hide" — a buffering or quality-switch cause can no longer appear here.
            Diag.Line($"[media.chrome] {(vis.ChromeVisible ? "show" : "hide")} cause={vis.LastCause} holds=0x{(int)vis.Holds:X}");
        }
        if (vis.CursorHidden) HideCursor(); else ShowCursor();
        double due = vis.NextWakeMs;
        if (double.IsPositiveInfinity(due)) { _armedDueMs = double.PositiveInfinity; return; }   // a stale armed wake just finds nothing due
        // Re-arm only when the deadline moves EARLIER (the base::Timer trick). A pointer moving at 1 kHz pushes the
        // deadline LATER on every sample, and a heap insert per sample would fill the timer queue with stale generations;
        // a later deadline is picked up when the armed wake fires and this re-arms from the machine's current value.
        if (due < _armedDueMs - 1.0)
        {
            _armedDueMs = due;
            _wake.RestartIn((float)Math.Max(0.0, due - now));
        }
    }

    private void OnWake() { _armedDueMs = double.PositiveInfinity; Sync(); }

    /// <summary>Every OS move/size loop of this window ends here (edge resizes too). Only a move THIS element requested
    /// matters: its end releases the WindowMove hold (the machine ignores a stray end) and stamps the double-click guard.</summary>
    private void OnMoveSizeEnded()
    {
        if (_moveInFlight) { _moveInFlight = false; _lastWindowMoveMs = Now(); }
        _vis?.WindowMoveEnded(Now());
        Sync();
    }

    /// <summary>The window lost activation: the user switched away, so this is a real leave wherever the pointer was
    /// (and the dispatcher has already dropped every cursor override).</summary>
    private void OnWindowBlur() { _vis?.PointerLeft(Now()); Sync(); }

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

    /// <summary>LostFocus fires BEFORE the next GotFocus, so a Tab between two transport buttons momentarily reports
    /// "focus left the chrome". Deferring one dispatcher tick lets the paired GotFocus cancel the departure.</summary>
    private void ResolveFocusOut()
    {
        if (!_focusOutPending) return;
        _focusOutPending = false;
        _vis?.SetKeyboardFocusInControls(false, Now());
        Sync();
    }

    /// <summary>KEYBOARD focus inside the chrome reveals it (with the long keyboard dwell) and HOLDS it — revealing
    /// without holding is a WCAG 2.4.7 failure: the focused control fades out from under the keyboard user.
    /// <para>But only KEYBOARD focus may hold. Focus also arrives here by pointer activation and, more importantly, by
    /// the overlay service RESTORING focus to the invoking button when a picker closes — treating that as a keyboard user
    /// would pin the chrome open forever after any mouse trip through the quality menu, which is the opposite of what
    /// every shipped player does. <see cref="NodeFlags.FocusVisual"/> is exactly this distinction
    /// (<c>InputDispatcher.SetFocus</c>'s <c>visual</c> argument: true for Tab/arrow navigation, false for pointer and
    /// programmatic moves) and its doc names reading it from a GotFocus handler as the sanctioned use.</para></summary>
    private void OnChromeFocusChanged(bool focused)
    {
        if (focused)
        {
            _focusOutPending = false;
            _vis?.SetKeyboardFocusInControls(IsKeyboardFocus(), Now());
            Sync();
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

    /// <summary>A routed pointer move over the player (frame-local DIP). The machine de-duplicates re-delivered
    /// coordinates (the video.js phantom-mousemove fix) and measures the hidden-chrome deadzone from the REST point.
    /// <para>A routed move arrives only while NO button is held (the dispatcher suppresses it during a press/capture),
    /// so it also proves a press has ended. A release over another owner (a transport button, outside the frame) never
    /// fires this frame's OnPointerReleased; without this the Pressed hold would pin the chrome until the pointer left.</para></summary>
    private void OnPointerMoved(Point2 p)
    {
        if (_vis is not { } vis) return;
        double now = Now();
        if ((vis.Holds & ChromeHold.Pressed) != 0) vis.SetPressed(false, now);
        vis.PointerMoved(p.X, p.Y, now);
        Sync();
    }

    /// <summary>Mount only. A surface can mount UNDER a still cursor — the pop-out opening beneath it, the remount a
    /// fullscreen toggle makes. The dispatcher re-delivers nothing for a pointer that has not moved, so the machine would
    /// treat it as outside and the idle cursor would stay up for the whole first cycle. Not activity: nothing reveals.</summary>
    private void SeedPointerPresence()
    {
        if (_vis is not { } vis || _hooks?.GetPointerPosition?.Invoke() is not { } p) return;
        var scene = _scene;
        if (scene is null || _playerRoot.IsNull || !scene.IsLive(_playerRoot)) return;
        RectF r = scene.AbsoluteRect(_playerRoot);
        if (!r.Contains(p)) return;
        vis.PointerPresent(p.X - r.X, p.Y - r.Y);
        Sync();
    }

    /// <summary>OnPointerExit on the frame fires for a REAL leave AND when an overlay scrim or a capture cancel (the OS
    /// move loop taking the press) takes hover while the pointer is still over the player; only the former may schedule
    /// a hide. The dispatcher's pointer (window DIP; null when outside the client or blurred) tells them apart — a Win32
    /// leave parks it off-screen.</summary>
    private bool PointerStillOverPlayer()
    {
        if (_hooks?.GetPointerPosition?.Invoke() is not { } p) return false;
        var scene = _scene;
        return scene is not null && !_playerRoot.IsNull && scene.IsLive(_playerRoot) && scene.AbsoluteRect(_playerRoot).Contains(p);
    }

    private Signal<int>? _startupPhase;
    // The rebuffer overlay's own delay (the startup ladder's 500 ms): true once a rebuffer has outlived it.
    private Signal<bool>? _bufferingShown;

    private void OnStartupSpinnerDue() => _startupPhase?.SetIfChanged(1);
    private void OnStartupDetailDue() => _startupPhase?.SetIfChanged(2);
    private void OnBufferingSpinnerDue() => _bufferingShown?.SetIfChanged(true);

    /// <summary>Mount: settle the startup ladder and let the machine publish its first state and arm its first wake.</summary>
    private void OnMounted()
    {
        _startupPhase?.SetIfChanged(0);
        Sync();
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

    /// <summary>The pointer is over the CONTROL PANEL, not merely over the element: resting on the play button must never
    /// hide the bar (and the cursor) out from under it — a resting pointer emits no moves, so this is a HOLD, not
    /// activity. Leaving the panel restarts the dwell.</summary>
    private void OnChromePointerMove(Point2 _) { _vis?.SetPointerOverControls(true, Now()); Sync(); }
    private void OnChromePointerExit() { _vis?.SetPointerOverControls(false, Now()); Sync(); }

    // ── the PlayerChromeFeed seam (PART A): a host's OWN on-media chrome, driving the SAME machine the element's own
    // transport would have. _vis is null before this element's first Render — every call below is a harmless no-op
    // then, and PlayerChromeFeed.Owner is cleared on unmount so nothing here runs afterwards either (see the binding
    // effect near the window-hooks effect below).
    internal void FeedActivity() { _vis?.Activity(ChromeActivity.Pointer, Now()); Sync(); }
    internal void FeedOverControls(bool over) { _vis?.SetPointerOverControls(over, Now()); Sync(); }
    internal void FeedPressed(bool pressed) { _vis?.SetPressed(pressed, Now()); Sync(); }
    internal void FeedScrubbing(bool scrubbing) { _vis?.SetScrubbing(scrubbing, Now()); Sync(); }
    /// <summary>The host's window began an OS move loop (a caption-region drag; <see cref="InputHooks.WindowMoveSizeBeganObserved"/>).
    /// <see cref="_moveInFlight"/> makes <see cref="OnMoveSizeEnded"/> release the hold.</summary>
    internal void FeedWindowMoveStarted() { _moveInFlight = true; _vis?.WindowMoveStarted(Now()); Sync(); }

    private int _renderCount;
    /// <summary>How many times <see cref="Render"/> has run for this instance. A diagnostic read by the render-diet gates
    /// (buffered-amount churn, resize frames and caption cues must not re-render the element); nothing else reads it.</summary>
    internal int RenderCount => _renderCount;

    public override Element Render()
    {
        _renderCount++;
        // IsFullscreenPresentation freezes at mount, so this conditional hook is stable for the instance's lifetime.
        var binding = IsFullscreenPresentation ? PresentationBinding : UseVideoSurface();
        _postToUi = UsePost();
        var hooks = UseContext(InputHooks.Current);
        var overlayService = UseContext(Overlay.Service);
        _hooks = hooks;
        var areaRef = UseRef<NodeHandle>(default);
        var holeRef = UseRef<NodeHandle>(default);
        var posterRef = UseRef<NodeHandle>(default);
        // Latched once a frame has ever been ready: across a source switch (Playing -> Opening on the SAME element)
        // the hole stays active and the poster (opacity 1) covers it, so the next source's first frame arrives by
        // crossfade instead of a subtree rebuild. Never cleared for the life of the mount — see holeActive below.
        var hadVideo = UseRef<bool>(false);
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
        _ = HostFullscreen?.Value;
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
        // BufferingInfo is NOT read here: the protected pump republishes it for every appended segment (percent moves each
        // time), and a raw read made the whole element re-render, reallocating its children, closures and context menu,
        // for a value Render only uses to decide WHETHER an overlay exists. The memo collapses it to (IsBuffering, Reason)
        // and cuts the notification off while that is unchanged; the percent the overlay SHOWS is read inside the overlay
        // leaf (MediaStatusDetail), so only that leaf re-renders. The caption cue is likewise read by its own leaf.
        var bufferGate = UseComputed(() => BufferingKeyOf(Player.Buffering.Value));
        VideoAspectMode aspect = aspectSig.Value;
        double customAspect = customAspectSig.Value;
        bool audioOnly = IsAudioOnly(natural);
        bool videoReady = IsVideoReady(natural, state);
        // PART B (poster-until-first-frame): `videoReady` says only "MF is no longer in Opening" — it says NOTHING
        // about whether a DECODED FRAME has ever reached the compositor. Gating the poster/hole on it alone punches
        // the erase hole (and drops the poster) the instant the state leaves Opening, up to ~1 s before the backend's
        // first frame actually lands — a black rectangle where the poster used to be.
        // Player.VideoSurface is IMediaPlayer's own documented contract for this: "IsNone until the first video
        // frame" (IMediaPlayer.cs) — the presenter's own readiness signal for the CURRENT open cycle, reset to None by
        // every fresh OpenAsync and flipped non-None only once the backend truly has a composited frame to present.
        // No dedicated FirstFrame/FrameReady EVENT exists on the seam; this is the closest (and only) existing signal
        // that answers the question, so `framePresented` — not `videoReady` — is what actually gates the poster/hole.
        // ...AND readiness is per SLOT, not just per player: a player's VideoSurface stays non-None across the lifetime of
        // its session, so a freshly mounted element (pop-out open, back to the main window, an uncovered docked card) would
        // see "presented" at once — and punch its hole and drop its poster before ITS OWN composited child exists (the
        // registry only creates it once a handle is bound, a frame or two later). The slot's own bound signal gates both.
        bool slotBound = binding.Bound.Value;
        bool framePresented = !audioOnly && !Player.VideoSurface.Value.IsNone && slotBound;
        // The hadVideo latch must ALSO key off framePresented, not videoReady: latching on state alone would let the
        // very defect this fixes back in on the FIRST ever open (hadVideo would latch true the instant state left
        // Opening, and `holeActive` below ORs it in — punching an erase hole with nothing yet composited behind it).
        if (framePresented) hadVideo.Value = true;
        // The hole stays active across a switch (Playing -> Opening on the same element) so the compositor keeps
        // presenting the outgoing frame while the poster crossfades over it — no erase-to-transparent flash and no
        // subtree rebuild. EXCEPTION: IsDecorative never latches — a decorative clip must never erase the caller's
        // own still before its own first frame ever arrives.
        bool holeActive = IsDecorative ? framePresented : !audioOnly && (framePresented || hadVideo.Value);
        // The poster is up whenever the element has not yet PRESENTED a ready frame; IsDecorative never shows one
        // (its "no poster" contract — the caller's own still shows through the transparent hole instead).
        bool posterUp = !IsDecorative && !framePresented;
        // "media-stage" is mounted iff !IsDecorative && ShowLetterboxBars — both frozen at mount (init props), so this
        // is a CONSTANT for the element's whole life: the fixed child shape below never gains or loses this slot.
        bool showStage = !IsDecorative && ShowLetterboxBars;

        // ── the video pump lives OUTSIDE Render (fix: pure Render). Publish the inputs it reads, register it once. ──
        _binding = binding;
        _areaRef = areaRef;
        _holeRef = holeRef;
        _aspectForPump = aspectSig;
        _customAspectForPump = customAspectSig;
        _scene = Context.Scene;
        // Peeked (never .Value) by the pump, so publishing it here does not make the pump a render dependency.
        _isActive = UseIsActive();

        // A LAYOUT effect, not a passive one: passive effects drain at the end of the frame (after PumpPending and the
        // present), so a pump registered there missed the mount frame and the slot's handle bind ran a frame later — the
        // window in which a fresh element showed its hole with no video child behind it. Layout effects run before the
        // frame's pump turn, so the first pump (RegisterPump requests it) lands in the mount frame.
        UseLayoutEffect(() =>
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

        // The area is a Render dependency ONLY for the transport's width tier. Without a transport (Wavee suppresses the
        // engine's: its own strip lives outside this element) the only other consumer is the hole's Margin below, and
        // SyncHoleLetterbox already writes that in the SAME solve the area changes in, so subscribing would just schedule
        // a redundant full re-render one frame behind every resize frame (pop-out live resize, PiP edge drag, splitters).
        // Peek still hands any re-render (a source switch, an aspect change) the last arranged area for the terminal Margin.
        bool transportMounted = AreTransportControlsEnabled && !SuppressTransport;
        RectF area = transportMounted ? areaBounds.Value : areaBounds.Peek();

        // ── the chrome machine: ONE pure policy, ONE timer, ONE sync ────────────────────────────────────────────────
        // Buffering, a stall, an ABR quality switch and Opening are deliberately NOT inputs: none of them reveals the
        // chrome and none holds it (Chromium's ShouldHideMediaControls has no buffering clause; Media3's timeout runs
        // "with playback or buffering in progress") — the status overlay speaks for them. The old machine let them REVEAL:
        // the protected session maps Licensed+Buffering onto PlaybackState.Buffering and ABR switches report
        // BufferingReason.QualitySwitch, so the controls popped up mid-playback with no user input at all.
        _wake = UseTimeout(_onWake, TransportControlsHideDelayMs, DepKey.Empty);   // arms once at mount — harmless (Sync re-arms)
        _settle = UseTimeout(_onGeometrySettled, GeometrySettleMs, DepKey.Empty);   // the geometry-motion trailing edge (see NoteGeometryMotion)
        // A LOCAL for the lambdas below: the field is nullable, and nullable flow analysis does not carry `??=` into a
        // lambda (CS8602 is an error under TreatWarningsAsErrors).
        var vis = _vis ??= new PlayerChromeVisibility(
            PlayerChromeTiming.Default with { IdleHideMs = TransportControlsHideDelayMs }, _wake.NowMs);

        // gate.media.el.transport-suppressed reads this exact spelling: auto-hide is gated on the transport existing.
        bool autoHideArmed = AreTransportControlsEnabled && AutoHideTransportControls && !SuppressTransport && !IsDecorative;
        // ...but the CURSOR is not the transport's. A host that draws its OWN on-media chrome turns this transport off
        // and still wants mpv's cursor: idle over the picture and the pointer goes away. Gating the machine on the
        // transport existing is why CursorAutoHide was inert on every suppressed surface — the Disabled hold pins
        // ChromeVisible true, and the cursor only ever hides once the conceal has finished. Running it for the cursor
        // costs one wake, armed only while the pointer is inside, and publishes a ChromeVisible the host may bind (it
        // is what its own strip should fade on, so the two can never disagree about what "idle" means).
        bool cursorArmed = !IsDecorative && AutoHideTransportControls && CursorAutoHide != CursorAutoHidePolicy.Never;
        // A host that binds ChromeVisibleOut is asking the machine to run FOR IT. Its strip is not our element, so
        // nothing else here can tell that idle still matters on this surface.
        bool hostChromeArmed = ChromeVisibleOut is not null && !IsDecorative && AutoHideTransportControls;
        bool a11y = IsAccessibilityActive?.Invoke() ?? false;
        UseEffect(() => { double t = Now(); vis.SetEnabled(autoHideArmed || cursorArmed || hostChromeArmed, t); vis.SetAccessibility(a11y, t); Sync(); },
            HashCode.Combine(autoHideArmed, cursorArmed || hostChromeArmed, a11y));

        // Only USER-VISIBLE stops reveal and hold (paused / ended / failed / audio-only) — see ChromePlaybackOf.
        ChromePlayback chromePlayback = ChromePlaybackOf(playIntent, state, audioOnly);
        UseEffect(() => { vis.SetPlayback(chromePlayback, Now()); Sync(); }, (int)chromePlayback);

        // The scrub gate (it lives in the seek bar and changes without re-rendering this element) and the menu gate:
        // EFFECTS, never the render. The menu clause covers ANY input-blocking overlay: such an overlay mounts a full-bleed
        // scrim that becomes the topmost hit target, so hover stops reaching the player while the dwell would run on
        // UNDER the menu; and the epoch drives an effect because subscribing to the host-global PinEpoch in Render
        // re-rendered the whole player for every menu opened anywhere in the shell.
        // With SuppressTransport set, seekBar is never MOUNTED (BuildTransport is skipped below), so its Scrubbing
        // signal never changes and this effect never fires — the ENGINE'S OWN rail is silent for the whole life of a
        // suppressed surface. PlayerChromeFeed.SetScrubbing (PART A) is then the Scrubbing hold's only writer, so the
        // two can never disagree about whether a scrub is in progress.
        UseSignalEffect(() => { bool scrubbing = seekBar.Scrubbing.Value; vis.SetScrubbing(scrubbing, Now()); Sync(); });
        UseSignalEffect(() =>
        {
            _ = overlayService.PinEpoch.Value;      // the change signal (not the decision)
            vis.SetMenuOpen(AnyInputBlockingOverlay(overlayService), Now());
            Sync();
        });

        // The cursor policy × presentation. Entering fullscreen with idle chrome hides the cursor IMMEDIATELY — waiting
        // for a move means it sits on top of a fullscreen frame until the user jiggles it, exactly when they are least
        // likely to; leaving it shows the cursor again (a windowed FullscreenOnly surface never hides it).
        bool presentedFullscreen = PresentingFullscreen || fullscreen.Value;   // subscribe: the policy edge must re-render
        UseEffect(() => { vis.SetCursorMayHide(CursorHidingAllowed(), Now()); Sync(); }, presentedFullscreen ? 1 : 0);

        // An OS move/size loop of the host window reports its end through the host; a window blur is a LEAVE (the
        // dispatcher clears hover before it drops the pointer position, so the exit alone would read as "covered" and
        // the controls would linger over a window the user has switched away from).
        UseEffect(() =>
        {
            if (hooks is null) return (Action?)null;
            hooks.WindowMoveSizeEndedObserved += _onMoveSizeEnded;
            hooks.WindowBlurObserved += _onWindowBlur;
            return () =>
            {
                hooks.WindowMoveSizeEndedObserved -= _onMoveSizeEnded;
                hooks.WindowBlurObserved -= _onWindowBlur;
            };
        }, DepKey.Empty);
        // PART A: claim ChromeFeed on mount, release it on unmount — but only if it is STILL this instance. A keyed
        // remount binds the NEW instance's Owner first (its own mount effect), so the OLD instance's cleanup (which
        // runs after, per the reconciler's mount-then-unmount ordering for a Key swap) must not null out the new claim.
        UseEffect(() =>
        {
            if (ChromeFeed is not { } feed) return (Action?)null;
            feed.Owner = this;
            return () => { if (feed.Owner == this) feed.Owner = null; };
        }, DepKey.Empty);
        UseEffect(OnMounted, DepKey.Empty);
        UseEffect(() => (Action?)ReleaseCursorOverride, DepKey.Empty);
        UseLayoutEffect(_seedPointerPresence, DepKey.Empty);   // after layout: the frame's rect is valid

        bool showChrome = AreTransportControlsEnabled && !SuppressTransport && chromeVisible.Value;

        // ── startup / first frame (Task 5) ───────────────────────────────────────────────────────────────────────────
        // 0 ms poster. 0-500 ms NOTHING (a spinner that flashes for 200 ms is worse than no spinner). 500 ms an animated
        // indeterminate ring. Past 10 s a determinate readout may appear. A mid-play REBUFFER is not a startup: it never
        // reaches this ladder, never shows the poster, and never resets the chrome.
        var startupPhase = UseSignal(0);
        _startupPhase = startupPhase;
        var startupSpinner = UseTimeout(OnStartupSpinnerDue, StartupSpinnerDelayMs, DepKey.Empty);
        var startupDetail = UseTimeout(OnStartupDetailDue, StartupDetailDelayMs, DepKey.Empty);
        // Off for a decorative element and for a host that owns its loading visuals: with no overlay to earn, the ladder's
        // 500 ms / 10 s timers and the re-renders they cause would only serve pixels the host covers.
        bool statusEnabled = !IsDecorative && ShowStatusOverlay;
        bool startingUp = statusEnabled && !videoReady && (playIntent || state is PlaybackState.Opening or PlaybackState.Buffering);
        UseEffect(() =>
        {
            if (startingUp) { startupSpinner.Restart(); startupDetail.Restart(); }
            else { startupSpinner.Cancel(); startupDetail.Cancel(); startupPhase.SetIfChanged(0); }
            return (Action?)null;
        }, startingUp ? 1 : 0);
        int phase = startupPhase.Value;

        // ── mid-play rebuffer ladder: the SAME 500 ms rule as the startup ladder above. A pill that flashes for 200 ms
        // reports trouble that did not happen, and a seek or quality switch behind a picture that is already up is not
        // trouble at all (the seek bar's own inline spinner covers a slow seek) — BufferingOverlayWanted says which.
        BufferingKey bufferNow = bufferGate.Value;
        bool bufferingWanted = statusEnabled
            && BufferingOverlayWanted(startingUp, bufferNow.IsBuffering, bufferNow.Reason, state, framePresented);
        // The render in which the start ends but the opening ring is still up (the ladder's reset runs in the passive drain,
        // so `phase` still holds the pre-reset value): a start that goes straight on to a rebuffer must not swap a visible
        // ring for 500 ms of nothing, so the pill takes over at once (see ChooseStatusOverlay).
        bool ladderHandoff = !startingUp && phase >= 1;
        var bufferingShown = UseSignal(false);
        _bufferingShown = bufferingShown;
        var bufferingSpinner = UseTimeout(OnBufferingSpinnerDue, StartupSpinnerDelayMs, DepKey.Empty);
        UseEffect(() =>
        {
            if (bufferingWanted)
            {
                if (ladderHandoff) bufferingShown.SetIfChanged(true);
                else bufferingSpinner.Restart();
            }
            else { bufferingSpinner.Cancel(); bufferingShown.SetIfChanged(false); }
            return (Action?)null;
        }, bufferingWanted ? 1 : 0);

        void RevealChrome()
        {
            _vis?.Activity(ChromeActivity.Pointer, Now());
            Sync();
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
            RevealChrome();                                        // a reveal shows the cursor too (and the policy edge re-applies)
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
                    ShowStatusOverlay = ShowStatusOverlay,
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
            switch (ExitRouteFor(IsFullscreenPresentation, HostPresenting, fullscreen.Peek()))
            {
                case FullscreenExit.OverlayPresentation: ExitFullscreen?.Invoke(); break;
                case FullscreenExit.Host: FullscreenRequested?.Invoke(); FullscreenChanged?.Invoke(false); break;
                case FullscreenExit.OwnOverlay: LeaveFullscreen(); break;
            }
        }

        // A terminal failure wins over the opening spinner: otherwise a Failed state with a lingering play intent would
        // keep showing "Starting playback…" forever (the DRM-license-rejected infinite-spinner bug).
        // Decoration never reports status, and a host that owns its loading visuals turns the whole overlay off.
        // The opening and rebuffer overlays are LEAVES (MediaStatusDetail): they read the live percent themselves, so a
        // buffered-amount tick re-renders the leaf and never this element.
        StatusOverlayKind statusKind = ChooseStatusOverlay(IsDecorative, !ShowStatusOverlay, state, startingUp, phase,
            bufferingWanted, bufferingShown.Value || ladderHandoff);
        Element? statusOverlay = statusKind switch
        {
            StatusOverlayKind.Failed => FailedOverlay(Player.Error.Value?.Message),
            StatusOverlayKind.Opening => Embed.Comp(new StatusProps(statusKind, playIntent, BufferingReason.None, phase == 2),
                () => new MediaStatusDetail { Player = Player }),
            StatusOverlayKind.Buffering => Embed.Comp(new StatusProps(statusKind, playIntent, bufferNow.Reason, false),
                () => new MediaStatusDetail { Player = Player }),
            _ => null,
        };

        // ── the video stage (a ZStack: children paint in author order) ───────────────────────────────────────────────
        // A FIXED keyed shape that NEVER changes across a source switch — the reconciler patches PROPS on the same
        // nodes instead of unmounting/remounting the subtree (E4):
        //   [0] "media-stage" — the OPAQUE LetterboxColor stage fill across the WHOLE video area (mounted iff
        //       !IsDecorative && ShowLetterboxBars — both frozen at mount, so this slot is a CONSTANT for the
        //       element's whole life, never toggled by state). This fill IS the letterbox — there are no separate bar
        //       elements.
        //   [1] "media-hole" — ALWAYS mounted. The VIDEO HOLE PUNCH (DrawOp.DrawVideo, gpu-renderer.md §7.3), laid
        //       out at EXACTLY the fitted video rect. VideoHole is a PROP (holeActive) that TOGGLES — never a
        //       presence change — so the node and its DComp registration survive a source switch. Active, it erases
        //       everything already recorded beneath it toward premultiplied zero, so the DComp video visual
        //       composited z-BELOW the premultiplied UI swapchain shows through at full strength instead of blending
        //       with what stayed in the back buffer. Inactive, it is an ordinary (non-erasing) transparent box.
        //   [2] "media-poster" — ALWAYS mounted, a LATER sibling of the hole. Visibility rides OPACITY (posterUp),
        //       never presence: at opacity 1 it fully covers the hole beneath it, so the crossfade seeded below IS
        //       the hand-off between whatever was showing (an outgoing frame across a switch, or nothing yet on the
        //       very first open) and the next ready frame — never a subtree rebuild.
        //   [3…] the status overlay — a LATER sibling still, so it repaints over the video. Presence-gated (it is
        //       genuinely transient), key preserved. Then "media-caption-slot" — ALWAYS mounted, a leaf that owns the
        //       caption: it reads the active cue itself, so cue changes never re-render this element.
        // ONE SOURCE OF TRUTH: PumpNow places the DComp visual from scene.AbsoluteTransformedRect of the "media-hole" node, so
        // the erased region and the presented video are the same rect BY CONSTRUCTION.
        bool showStatusOverlay = statusOverlay is not null;
        var videoChildren = new Element[(showStage ? 1 : 0) + 2 + (showStatusOverlay ? 1 : 0) + 1];
        int vc = 0;
        if (showStage)
            videoChildren[vc++] = new BoxEl { Key = "media-stage", Grow = 1f, Fill = LetterboxColor, HitTestVisible = false };
        videoChildren[vc++] = new BoxEl
        {
            Key = "media-hole",
            Grow = 1f,
            AlignSelf = FlexAlign.Start,
            // area MINUS these insets == the fitted video rect. This is the TERMINAL (it is right whenever `area` is
            // current, and it is what the reconciler re-asserts on every patch); the SAME-SOLVE corrector for the frame
            // the area itself changes on is SyncHoleLetterbox, called from the area's OnBoundsChanged below.
            Margin = HoleInsets(area, natural, aspect, customAspect, _lastPumpScale),
            VideoHole = holeActive,
            VideoSurfaceId = binding.Token,
            OnRealized = h => { holeRef.Value = h; binding.RequestPump(); },
        };
        // The poster stays up for the WHOLE start — including the quiet first 500 ms and the spinner phase, AND for
        // the Opening leg of a source switch (the hole latches active underneath it — see holeActive above). It is
        // never replaced by a black rect: in a music app the poster IS the album art already on screen, and swapping
        // it for darkness to host a spinner is a visible regression, not a loading state. The static Opacity below is
        // the crossfade's TERMINAL value; the UseLayoutEffect further down seeds the eased approach to it (honors
        // reduced-motion, the same idiom the chrome fade uses).
        videoChildren[vc++] = new BoxEl
        {
            Key = "media-poster",
            Grow = 1f, ZStack = true, Direction = 1,
            HitTestVisible = false,
            Opacity = posterUp ? 1f : 0f,
            OnRealized = h => posterRef.Value = h,
            Children = [PosterContent ?? DefaultPoster()],
        };
        if (showStatusOverlay)
            videoChildren[vc++] = new BoxEl
            {
                Key = videoReady ? "media-buffering" : "media-opening",
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Animate = LoadingMotion,
                Children = [statusOverlay!],
            };
        // Captions MOVE, controls do not: the caption baseline lifts by the chrome's measured height while the chrome
        // is up and settles back when it hides, animated on the same clock (a transform-only FLIP — no relayout churn).
        // The caption slot is PERMANENTLY mounted (a fixed shape, like the stage/hole/poster): its leaf reads the active
        // cue and the chrome-height lift itself, so a cue start or clear re-renders the leaf and never this element.
        videoChildren[vc++] = Embed.Comp(() => new MediaCaptionOverlay
        {
            Player = Player,
            ChromeVisible = chromeVisible,
            ChromeHeight = chromeHeight,
            TransportPresent = AreTransportControlsEnabled && !SuppressTransport,
        }) with { Key = "media-caption-slot" };

        var videoArea = new BoxEl
        {
            ZStack = true,
            Direction = 1,
            Grow = 1f,
            Shrink = 1f, MinWidth = 0f,
            MinHeight = VideoAreaMinHeight(PresentingFullscreen, IsDecorative),
            ClipToBounds = true,
            Corners = FrameCorners,
            Fill = ColorF.Transparent,
            OnRealized = h => { areaRef.Value = h; binding.RequestPump(); },
            OnBoundsChanged = b =>
            {
                // SAME-SOLVE letterbox (the resize-desync fix). FlexLayout delivers this from SetArrangedBounds for the
                // AREA (FlexLayout.cs:718) BEFORE it recurses into this ZStack's children (:723), so writing the hole's
                // Margin into the layout column here places the hole at the fitted rect IN THIS PASS. The signal write
                // below can never do that: a value written during layout only marks stale, so its consumer re-renders
                // NEXT frame (RenderContext.Measure.cs:18-20) — which is precisely how the picture came to lag the
                // chrome by a frame (and, when the stale hole overflowed the clipped card, to be squashed by the pump's
                // overflow net). The signal write stays: the transport's width tier reads it (and only while the transport is mounted).
                SyncHoleLetterbox(b);
                if (b != areaBounds.Peek()) areaBounds.Value = b;
                // GEOMETRY-class: a layout-driven rect change (a PiP edge resize, a pop-out live resize, a reflow) is the same
                // motion as a compositor move, so a frame of it places the surface and stops (PumpVideo and the log line
                // wait for the settle timer); the pump itself escalates when the downscale cap moves or no size is cached.
                binding.RequestGeometryPump();
            },   // resize → same-frame letterbox + one settled video placement
            Children = videoChildren,
        };

        var layers = new Element[1 + (AreTransportControlsEnabled && !SuppressTransport ? 1 : 0) + (shortcutsOpen.Value ? 1 : 0)];
        int lc = 0;
        layers[lc++] = videoArea;
        if (AreTransportControlsEnabled && !SuppressTransport)
        {
            // The chrome stays MOUNTED across the whole hide cycle, with a STABLE Key. Toggling it in and out of the
            // layer list unmounted the entire subtree — including the MediaSeekBar — so every re-reveal built a FRESH
            // bar with width 0 and fraction 0: the thumb sat at the far left and an empty rail flashed for at least one
            // frame on EVERY auto-hide cycle. Visibility now rides the opacity + hit-test + focusability channel, which
            // is also what keeps hidden chrome out of the hit-test, focus and accessibility trees rather than merely
            // transparent (an invisible-but-hittable control panel eats clicks meant for the video).
            layers[lc++] = new BoxEl
            {
                Key = "media-chrome",
                Grow = 1f,
                Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Direction = 1,
                Justify = FlexJustify.End,
                HitTestPassThrough = true,
                HitTestVisible = showChrome,
                Opacity = showChrome ? 1f : 0f,
                OnRealized = h => chromeRef.Value = h,
                Children = [BuildTransport(area.W, showChrome, seekBar, chromeHeight, volumeExpanded,
                    ToggleFullscreen, ccAnchor, qualityAnchor, rateAnchor, audioAnchor, overlayService)],
            };
        }
        if (shortcutsOpen.Value) layers[lc++] = ShortcutOverlay(() => shortcutsOpen.Value = false);

        // The fade itself: asymmetric by token (reveal 150 ms decelerate, conceal 400 ms ease-out), reduced motion resolved
        // as a VALUE by MotionTokenDef.EffectiveDurationMs — never as a branch here. The static Opacity above is the
        // TERMINAL (so an unrelated re-render — an ABR label, a cue — always re-asserts the right end value); this seeds
        // the eased approach to it.
        // FROM = where the pixels ARE. A layout effect runs AFTER the reconciler has re-asserted the static terminal (it
        // writes the element's Opacity into the paint on every patch), so the old `scene.Paint(node).Opacity` read
        // returned `to` and the fade degenerated into a one-frame CUT in both directions; under render-owned compositor
        // rows the UI paint holds the authored base anyway. A live row (an interrupted fade) carries the real value — a
        // reveal that interrupts a conceal reverses from wherever the pixels are; with no row the pixels sit at the
        // PREVIOUS terminal. The mount run seeds nothing: the chrome is born at its terminal.
        var chromeFadeArmed = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!chromeFadeArmed.Value) { chromeFadeArmed.Value = true; return; }
            var node = chromeRef.Value;
            var scene = Context.Scene;
            if (Context.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
            var tok = showChrome ? MotionTok.MediaChromeReveal : MotionTok.MediaChromeConceal;
            float to = showChrome ? 1f : 0f;
            float ms = tok.EffectiveDurationMs(AnimChannel.Opacity);
            if (ms <= 0f) return;    // reduced motion: the static Opacity above already IS the terminal value
            float from = anim.TryGetTrackValue(node, AnimChannel.Opacity, out float live) ? live : 1f - to;
            if (MathF.Abs(from - to) < 0.001f) return;
            anim.SeedEased(node, AnimChannel.Opacity, from, to, ms, tok.Easing);   // the anim tick composes `from` this very frame
        }, showChrome ? 1 : 0);

        // The poster's hand-off: the SAME seed rule as the chrome fade above (it had the identical read-after-reassert
        // cut). A switch that re-arms the crossfade mid-fade departs from the live value; the mount run seeds nothing —
        // a remounted element must never fade a poster in over video that is already decoded, or out over nothing.
        var posterFadeArmed = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!posterFadeArmed.Value) { posterFadeArmed.Value = true; return; }
            var node = posterRef.Value;
            var scene = Context.Scene;
            if (Context.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
            float to = posterUp ? 1f : 0f;
            float ms = PosterCrossFade.EffectiveDurationMs(AnimChannel.Opacity);
            if (ms <= 0f) return;    // reduced motion: the static Opacity above already IS the terminal value
            float from = anim.TryGetTrackValue(node, AnimChannel.Opacity, out float live) ? live : 1f - to;
            if (MathF.Abs(from - to) < 0.001f) return;
            anim.SeedEased(node, AnimChannel.Opacity, from, to, ms, PosterCrossFade.Easing);
        }, posterUp ? 1 : 0);

        void HandleKey(KeyEventArgs e) => HandleKeyCore(e, seekBar, ToggleFullscreen, ExitFullscreenOnly, shortcutsOpen);

        void HandlePress(PointerEventArgs e)
        {
            double now = Now();
            _pressIsTouch = e.Kind == PointerKind.Touch;
            // Middle-button = mute (delivered on release with Button == 2, the WinUI commit-on-release shape).
            if (e.Button == 2) { Player.SetMuted(!Player.Muted.Peek()); _vis?.Activity(ChromeActivity.Pointer, now); Sync(); return; }
            // A DOUBLE click toggles fullscreen (mpv MBTN_LEFT_DBL, Firefox PiP). A SINGLE click deliberately does NOT
            // toggle play: the alternative is a delayed single-click that costs GetDoubleClickTime() (500 ms on Windows)
            // on EVERY play/pause and produces the notorious "video toggles state while going fullscreen" bug — and a
            // click that raises/focuses a window must not pause it (mpv #15405). YouTube, Netflix and Vimeo all chose
            // reveal-only; play/pause lives on the button, Space and K. A press right after a window drag is never the
            // second half of a double-click (see _lastWindowMoveMs).
            if (e.ClickCount >= 2 && now - _lastWindowMoveMs > FluentGpu.Input.InputDispatcher.DoubleClickMs)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            // Touch TOGGLES (Media3 hide_on_touch, Chromium kGestureTap); a mouse/pen press reveals and holds while down.
            if (_pressIsTouch) _vis?.Tapped(now); else _vis?.SetPressed(true, now);
            Sync();
        }

        void HandleRelease(PointerEventArgs e)
        {
            _vis?.SetPressed(false, Now());
            Sync();
        }

        void HandleExit()
        {
            // A real leave (the window, the non-client resize band, a blur) hides after the short leave debounce; hover
            // TAKEN over the player (a scrim, the OS move loop's capture cancel) only drops the pointer holds.
            if (PointerStillOverPlayer()) _vis?.PointerCovered(Now());
            else _vis?.PointerLeft(Now());   // also shows the cursor: it must never stay hidden outside the video
            Sync();
        }

        void HandleWheel(WheelEventArgs e)
        {
            // Decorative clips are never operated by the user (IsDecorative: "an artist portrait, a hover preview —
            // never for a player the user operates") — like the missing context menu and transport above, the wheel
            // passes straight through so the page underneath a decorative surface can always scroll.
            if (IsDecorative) return;

            // Shift+wheel seeks (unchanged). Alt+wheel is volume — Ctrl stays reserved for the app's window-zoom
            // gesture (bound at the dispatcher and in the host shell's Shell.UI.cs), so a Ctrl+wheel is left
            // unhandled here and bubbles on, the same way FlipView ignores it (FlipView.cs:193). A PLAIN wheel (no
            // modifier) belongs to the page the player is docked in: it falls back to volume only when there is
            // nothing left to scroll — fullscreen presentation — matching how every browser, YouTube and Spotify
            // treat a wheel over a docked/inline video. The seek bar consumes its own wheel first, so a wheel over
            // the rail never reaches here.
            bool shift = (e.Mods & KeyModifiers.Shift) != 0;
            bool alt = (e.Mods & KeyModifiers.Alt) != 0;
            bool ctrl = (e.Mods & KeyModifiers.Ctrl) != 0;
            if (shift) seekBar.SeekBy(e.Delta > 0f ? 10f : -10f);
            else if (!ctrl && (alt || PresentingFullscreen)) AdjustVolume(e.Delta > 0f ? VolumeStep : -VolumeStep);
            else return;   // not a player gesture: leave Handled=false so it bubbles (ambient scroller, or app zoom)

            _vis?.Activity(ChromeActivity.Pointer, Now());
            Sync();
            e.Handled = true;
        }

        var frame = new BoxEl
        {
            ZStack = true,
            Grow = 1f,
            // Shrinkable + zero min: the element must YIELD to its host card, never widen it. FlexShrink defaults
            // to 0, so without this the transport row's intrinsic width (~500 DIP expanded) became the element's
            // floor — the card overflowed sideways, the video composited at the overflowed rect, and the compact
            // decision (fed the element's own width) latched non-compact forever.
            Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Corners = FrameCorners,
            ClipToBounds = true,
            BorderColor = PresentingFullscreen || IsDecorative ? ColorF.Transparent : Tok.StrokeFlyoutDefault,
            BorderWidth = PresentingFullscreen || IsDecorative ? 0f : 1f,
            Focusable = true,
            OnRealized = h => { playerRoot.Value = h; _playerRoot = h; },
            OnKeyDown = HandleKey,
            OnPointerMoveWithin = _onPointerMoved,
            OnPointerPressed = HandlePress,
            OnPointerReleased = HandleRelease,
            OnPointerExit = HandleExit,
            OnPointerWheel = HandleWheel,
            // Only KEYBOARD focus on the player is activity (it reveals with the long dwell); pointer/programmatic focus
            // (a press, a surface parking focus here at mount) is not.
            OnFocusChanged = focused => { if (focused && IsKeyboardFocus()) { _vis?.Activity(ChromeActivity.Keyboard, Now()); Sync(); } },
            Children = layers,
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
    /// <see cref="VideoAspectMode.UniformToFill"/> crop or a <see cref="VideoAspectMode.Native"/> 1:1 frame, either of
    /// whose fitted rect deliberately overflows the stage.</para></summary>
    private void PumpNow(float scale) => PumpCore(scale, refit: false);

    /// <summary>The pump body. <paramref name="refit"/> is set only for the ONE same-pump follow-up that a natural-size
    /// change inside <see cref="IMediaPlayer.PumpVideo"/> asks for (<see cref="PumpNow"/> never sets it, and the follow-up
    /// never asks again, so it cannot loop): the hole is still laid out from the PREVIOUS natural size, so the rect comes
    /// from the fit of the NEW size instead of from the hole node.</summary>
    private void PumpCore(float scale, bool refit)
    {
        VideoBinding b = _binding;
        if (!b.IsValid) return;
        // Parked (Flow.KeepAlive) or minimized: hide the composited surface. The video visual lives in its OWN
        // DirectComposition visual below the UI swapchain, so it is not clipped or covered by whatever the shell draws
        // next — a parked page's frame keeps compositing at its last placement (a navigated-away artist portrait on the
        // nav rail). Decorative clips SKIP the pump while inactive (they must not keep an MF session alive off-screen).
        // Non-decorative player surfaces (PiP / pop-out) still pump: the MF session only advances while pumped, and
        // NaturalSize / duration never publish without it — hiding without pumping is the black Loading poster over audio.
        // VISIBILITY IS THIS ELEMENT'S DECISION, written LAST on every path: sessions only express readiness (they place
        // content and bind the handle, they never show the surface), so nothing pumped below can overwrite the hide.
        // The inactive pump is STATE-ONLY: the inert default binding (IsValid false) makes every session skip its
        // bind / Place / SetVisible(true) / stream-size step, so position, state, Ended and errors keep publishing while
        // the slot stays hidden at its last geometry. It must NOT fall through to the placed pump below: a presence-
        // collapsed surface keeps stale non-zero descendant bounds but is clipped to 0x0 by its own ancestor, so that
        // path would bail on the empty viewport before ever reaching PumpVideo, and a placed pump from a covered or
        // handed-off presenter would re-show its visual and fight the active one over the stream size. Uncovering
        // re-places through the activation pump.
        bool active = _isActive?.Peek() ?? true;
        float s = scale <= 0f ? 1f : scale;
        _lastPumpScale = s;
        if (!active)
        {
            b.SetVisible(false);
            if (IsDecorative) return;
            Player.PumpVideo(default, default, s);
            return;
        }
        var scene = _scene;
        NodeHandle h = _areaRef?.Value ?? default;
        // Non-decorative player surfaces must keep calling PumpVideo even before the area is laid out (remount /
        // generation swap frames): MF only publishes duration + NaturalSize inside PumpVideo. Returning early here is
        // what left a video→video successor stuck on the Opening/Loading poster at 0:00 with no duration adopt.
        if (scene is null || h.IsNull || !scene.IsLive(h))
        {
            if (!IsDecorative) Player.PumpVideo(b, default, s);
            b.SetVisible(false);    // no laid-out area ⇒ empty viewport
            return;
        }
        // The TRANSFORMED rect (full affine walk), not the translation-only AbsoluteRect: under a scaled ancestor the hole is
        // painted through the whole world transform, and a placement from the unscaled rect would overhang or gap it.
        RectF area = scene.AbsoluteTransformedRect(h);
        if (area.W <= 0f || area.H <= 0f)
        {
            if (!IsDecorative) Player.PumpVideo(b, default, s);
            b.SetVisible(false);
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
            videoRect = live ? scene.AbsoluteTransformedRect(hole) : default;
            if (live) geom = hole;
            if (!live || videoRect.W <= 0f || videoRect.H <= 0f)
                videoRect = FitVideoRectSnapped(area, natural, mode, customAspect, s);
            else if (refit || ModeMayOverflow(mode))
            {
                // REFIT (the natural size changed inside PumpVideo): the hole node is still laid out from the previous
                // natural size, so its rect is the OLD fit. The new fit is computed from the same insets
                // SyncHoleLetterbox is about to write as the hole's Margin, so the next layout lands on exactly this rect.
                // CENTER-CROP and NATIVE are the two modes whose fitted rect deliberately OVERFLOWS the stage: crop
                // scales the frame until it covers the area, native is simply the frame's own device-pixel size —
                // either way the excess is clipped (by the viewport, below). The hole node carries that overflow as
                // NEGATIVE margins (LetterboxInsets is signed for exactly this), but a layout that clamps a negative
                // margin to zero hands back the stage rect itself — and placing the video at the stage rect scales
                // the frame DOWN to fit instead of cropping it, which is the overflow mode silently behaving like
                // Fill/Uniform. Recomputing the fit here is right either way: when the margins do survive layout
                // this is the same rect the hole already has. Built from the hole's OWN insets (HoleInsets over the
                // area's parent-relative layout bounds, the value SyncHoleLetterbox writes as the hole's Margin) rather
                // than a fit snapped against the ABSOLUTE area: the device-pixel snap is not translation-invariant, so
                // at a fractional absolute origin the two would round an edge differently and leave a one-pixel sliver
                // between the erased hole and the video.
                RectF layoutArea = scene.Bounds(h);
                videoRect = RectFromHoleInsets(area, layoutArea, HoleInsets(layoutArea, natural, mode, customAspect, s));
                if (ModeMayOverflow(mode)) geom = h;   // the rect now derives from the AREA, so follow the area's geometry
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
        // Overflow safety net: a layout defect that widens the element past its DIRECT host must degrade to a SMALLER
        // video, never to a video composited at a rect that is not on screen (plus an oversized ABR request). It is keyed
        // to the direct host's laid-out rect and NOT to the clip viewport: a scroller or the window edge cutting a
        // correctly laid-out player must not shrink and re-centre the video away from its hole. That case is the
        // viewport's job (SetViewport above crops the full fitted rect, as crop and native do).
        VideoAspectMode pumpMode = _aspectForPump?.Peek() ?? VideoAspectMode.Uniform;
        if (PumpClampsOverflow(pumpMode))
        {
            NodeHandle host = scene.Parent(h);
            if (!host.IsNull && scene.IsLive(host))
            {
                RectF hostRect = scene.AbsoluteTransformedRect(host);
                if (ExceedsDirectHost(videoRect, hostRect))
                    videoRect = ClampUniformToViewport(videoRect, hostRect);
            }
        }
        // GEOMETRY-ONLY TURN: this pump was requested only because the rect moved (RequestGeometryPumps — a drag, a resize,
        // an animated placement), so nothing the session publishes changed: state, buffering, position, cue, the stream
        // rect and the ABR bookkeeping are all driven by native events and transport commands, which request a FULL pump.
        // Place the surface and stop — no PumpVideo, no per-frame Position write fanning out to every subscriber. The
        // cached content size is only valid while it is still the size this rect needs (a pure translation never changes
        // it; a resize inside the stream-size bucket does not either; a resize that crosses into another bucket does),
        // otherwise the turn falls through to the full pump so the stream is never left sized for a stale rect (the session's
        // size gate then holds the stream until the rect is stable). The settle timer then runs the full pump once the motion
        // has ended.
        // A surface with no content size yet (not bound / sized) is not in motion: its turn is the full pump that sizes it.
        SizeI cached = b.IsGeometryOnlyPump ? b.ContentSize : SizeI.Zero;
        bool motion = !cached.IsEmpty;
        if (motion)
        {
            NoteGeometryMotion();
            if (!audioOnly && VideoStreamSizing.Serves(cached, natural, videoRect, s))
            {
                b.Place(videoRect);
                b.SetVisible(active && !Player.VideoSurface.Peek().IsNone);   // the viewport is non-empty and the frame has video here (checked above); AND the session's per-attach readiness
                return;
            }
        }
        LogPump(pumpMode, area, natural, videoRect, viewport, s, defer: motion || _settleArmed);
        Player.SetAdaptiveViewportHeight((int)MathF.Ceiling(videoRect.H * MathF.Max(1f, s)));
        Player.PumpVideo(b, videoRect, s);
        // The first metadata, a FORMATCHANGE or a different-aspect ABR rung publishes a new natural size INSIDE the pump,
        // but this pump placed against the hole as laid out for the old one: the new frame would be squashed into the old
        // fit until the next render + layout. Refit and re-place in THIS pump, and write the hole's Margin now so the next
        // layout lands without waiting for a render. Value-gated (one follow-up at most), so it cannot loop pumps.
        if (!refit && Player.NaturalSize.Peek() != natural)
        {
            SyncHoleLetterbox(scene.Bounds(h));
            PumpCore(s, refit: true);
            return;
        }
        b.SetVisible(active && !audioOnly && !Player.VideoSurface.Peek().IsNone);   // the viewport is non-empty here (the early-out above returns hidden); the session publishes readiness (VideoSurface) inside PumpVideo, so a re-attached or detached slot stays hidden until its own first frame
        if (!audioOnly) NoteSurfacePresented(b);
    }

    /// <summary>The SAME-SOLVE letterbox: place the video hole at the fitted rect for <paramref name="area"/> inside the
    /// layout pass that just arranged the area, by writing the hole's <c>Margin</c> straight into the layout column.
    ///
    /// <para><b>Why this and not a bound prop.</b> The fit needs BOTH extents of the area, so it cannot be expressed as
    /// a flex rule (<c>AspectRatio</c> derives the missing extent from the offered WIDTH only — a width-driven aspect
    /// box overflows a short host instead of pillarboxing in it). It therefore has to be computed from a measured rect,
    /// and every render-side road to that rect is one frame late: the area's rect only becomes available during LAYOUT,
    /// and a value written then merely marks stale, so its reader re-renders NEXT frame
    /// (<c>RenderContext.Measure.cs:18-20</c>). A bound <c>Margin</c> would not change that even if <c>Margin</c> were a
    /// <c>Prop&lt;Edges4&gt;</c> (it is a plain value — <c>Dsl/Element.cs:111</c>): bound layout props are flush-phase
    /// effects (<c>Reconciler.cs:2416-2447</c>) and the flush runs BEFORE layout, so an effect fed by a layout-written
    /// signal still lands a frame later. The one place that is both after the area's arrange and before the hole's is
    /// the area's own bounds-changed dispatch: <c>FlexLayout.SetArrangedBounds</c> fires it (<c>FlexLayout.cs:374-392</c>)
    /// from the top of <c>Arrange</c> (<c>:718</c>), and the ZStack recursion into the children follows at <c>:723</c>,
    /// reading each child's <c>Margin</c> out of the column it snapshots there (<c>:1363-1377</c>). So this write lands
    /// in the same solve that moved the area — the hole and the sibling chrome move together, in one frame.</para>
    ///
    /// <para>Value-gated, and it marks the hole <c>LayoutDirty</c> exactly like the engine's own bound layout-prop
    /// effects do. The mark is consumed by this same frame (the host clears it right after <c>_layout.Run</c>,
    /// <c>AppHost.cs:3729</c>), so it can never become a per-frame relayout. Zero managed allocation.</para></summary>
    private void SyncHoleLetterbox(in RectF area)
    {
        var scene = _scene;
        NodeHandle hole = _holeRef?.Value ?? default;
        if (scene is null || hole.IsNull || !scene.IsLive(hole)) return;
        Edges4 next = HoleInsets(area, Player.NaturalSize.Peek(),
            _aspectForPump?.Peek() ?? VideoAspectMode.Uniform,
            _customAspectForPump?.Peek() ?? (16.0 / 9.0), _lastPumpScale);
        // Two indexed touches instead of one held `ref`: a ref into the SoA column must not be alive across a call that
        // can touch the store (ArrangeZStack's own snapshot comment makes the same rule explicit).
        if (scene.Layout(hole).Margin == next) return;
        scene.Layout(hole).Margin = next;
        scene.Mark(hole, NodeFlags.LayoutDirty);
    }

    void LogPump(VideoAspectMode mode, RectF area, SizeI natural, RectF videoRect, RectF viewport, float scale, bool defer)
    {
        // Geometry motion changes this tuple every frame: only the settled geometry is worth a line. While a motion burst
        // is open (settle armed) no pump logs, geometry-only or full alike (a native/transport pump mid-drag would write
        // unsettled geometry); the trailing settle pump is the only one that logs, so one drag or resize is one Info line.
        if (defer) return;
        var line = (
            Mode: mode,
            Aw: (int)area.W, Ah: (int)area.H,
            Nw: natural.Width, Nh: natural.Height,
            Vw: (int)viewport.W, Vh: (int)viewport.H,
            Rw: (int)videoRect.W, Rh: (int)videoRect.H,
            Host: PresentingFullscreen && !IsFullscreenPresentation ? 1 : 0,
            Pres: IsFullscreenPresentation ? 1 : 0);
        if (line.Equals(_loggedPump)) return;
        _loggedPump = line;
        Diag.Line($"[video] pump mode={mode} natural={natural.Width}x{natural.Height} area={(int)area.W}x{(int)area.H} viewport={(int)viewport.W}x{(int)viewport.H} videoRect={(int)videoRect.X},{(int)videoRect.Y} {(int)videoRect.W}x{(int)videoRect.H} hostFs={line.Host} overlayFs={line.Pres} scale={scale:0.##}");
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
            ref NodePaint pp = ref scene.Paint(p);
            RectF lb = scene.Bounds(p);
            float cw = float.IsNaN(pp.PresentedW) ? lb.W : pp.PresentedW;
            float ch = float.IsNaN(pp.PresentedH) ? lb.H : pp.PresentedH;
            // Mapped through the ancestor's full transform (a scaled clipping ancestor clips a scaled rect), with the
            // presented extent as its local size.
            RectF c = scene.AbsoluteTransformedRect(p, cw, ch);
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
        // Hysteresis, not a single threshold: the row's own width feeds this decision, so a knife-edge threshold can
        // self-latch (un-compacting widens the row, which keeps it un-compact). Compact below the threshold;
        // un-compact only once comfortably past it.
        bool compact = measured && (areaWidth < CompactTransportWidth
            || (_transportCompact && areaWidth < CompactTransportExitWidth));
        _transportCompact = compact;
        bool presentingFullscreen = PresentingFullscreen;

        // Every control below carries a stable Key: the list is unkeyed-shape-changing across the "measured"/"compact"
        // transitions (chips appear/disappear once the row's first real measurement lands), and the reconciler matches
        // UNKEYED children POSITIONALLY by index + element type (Reconciler.ReconcileChildren) — IconButton and
        // TextButton are both BoxEl{TextEl}, so a shape change silently patches one logical button's node into another's
        // slot instead of mounting/unmounting. That starves whichever button loses its slot of an OnRealized call, so
        // its captured anchor handle (rateAnchor/qualityAnchor/ccAnchor/audioAnchor) stays default/stale and every click
        // on it is a silent no-op — the "speed/quality flyout doesn't open" bug. A Key makes every button's identity
        // explicit regardless of what else in the row appears or disappears that frame.
        var playPause = IconButton(playIntent ? Icons.Pause : Icons.Play, () =>
        {
            if (playIntent) RequestPause(); else RequestPlay();
        }, interactive) with { Key = "ctl:play" };

        var back10 = IconButton(Icons.Back, () => seekBar.SeekBy(-10f), interactive) with { Key = "ctl:back10" };
        var fwd10 = IconButton(Icons.Forward, () => seekBar.SeekBy(10f), interactive) with { Key = "ctl:fwd10" };

        // Volume: an icon that is a real mute toggle, plus a slider that expands on hover/focus. A bare mute toggle with
        // no slider is the one control users cannot substitute with anything else on the surface.
        var volumeChildren = new System.Collections.Generic.List<Element>(2)
        {
            IconButton(muted || Player.Volume.Peek() <= 0f ? Icons.Mute : Icons.Volume,
                () => Player.SetMuted(!muted), interactive),
        };
        // Gated on the hover-expansion ONLY, never on `interactive`: input is already gated by the chrome root's
        // HitTestVisible, and unmounting the slider at the hide edge reflowed the control row in the middle of its fade.
        if (volumeOpen)
            volumeChildren.Add(new BoxEl
            {
                Width = 84f, AlignItems = FlexAlign.Center,
                Children = [Slider.Create(Player.Volume, v => Player.SetVolume(v), length: 84f, thickness: 24f)],
            });
        var volume = new BoxEl
        {
            Key = "ctl:volume",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            OnPointerMoveWithin = _ => { volumeExpanded.Value = true; OnChromePointerMove(default); },
            OnPointerExit = () => volumeExpanded.Value = false,
            Children = volumeChildren.ToArray(),
        };

        var controls = new System.Collections.Generic.List<Element>(14) { playPause, back10, fwd10, volume };
        if (measured && areaWidth >= CompactTransportWidth)
            controls.Add(Embed.Comp(() => new MediaTransportTime { Player = Player, ScrubTargetSeconds = seekBar.ScrubTargetSeconds }) with { Key = "ctl:time" });
        controls.Add(new BoxEl { Key = "ctl:spacer", Grow = 1f, MinWidth = 0f });
        // A live source says so on the surface, next to the control that acts on it. The chip is a STATUS READOUT, not a
        // button: it is what answers "is this stream live or a recording" while the transport is showing no duration and
        // no seek bar at all — the state the user would otherwise have to infer from an absence. The Go-Live button
        // beside it stays the only interactive element (and reads "● LIVE" once the playhead is at the edge, so the two
        // never claim different things: the chip states the SOURCE is live, the button states where the PLAYHEAD is).
        if (timeline.IsLive)
            controls.Add(LiveChip() with { Key = "ctl:live-chip" });
        if ((commands & MediaCommandFlags.GoLive) != 0)
            controls.Add(TextButton(timeline.IsAtLiveEdge ? MediaStrings.LiveEdge : MediaStrings.GoLive,
                () => _ = Player.GoLiveAsync(), interactive, timeline.IsAtLiveEdge) with { Key = "ctl:go-live" });
        // Each chip renders its CURRENT VALUE, never a category label: "1×" not "Speed", "1080p" not "Quality",
        // "CC EN" not "Subtitles". A chip that names its category makes the user open it to find out what it is set to.
        // Each opens a PICKER flyout at its own anchor (never blind-cycles through the options).
        if (measured && !compact && (commands & MediaCommandFlags.Rate) != 0)
            controls.Add(TextButton(MediaStrings.RateLabel(rate), () => OpenPicker(rateAnchor, SpeedItems()), interactive)
                with { Key = "ctl:rate", OnRealized = h => rateAnchor.Value = h });
        // The quality chip SURVIVES compaction. It is the readout for the "why am I watching 240p" question, and the
        // one chip whose value the user cannot infer from anything else on screen.
        if (measured && (commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
            controls.Add(TextButton(QualityLabel(quality, activeQuality), () => OpenPicker(qualityAnchor, QualityItems()), interactive)
                with { Key = "ctl:quality", OnRealized = h => qualityAnchor.Value = h });
        if (measured && !compact && (commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
            controls.Add(TextButton(text is null ? MediaStrings.CaptionsShort : MediaStrings.CaptionsFor(text.Language ?? text.Label),
                () => OpenPicker(ccAnchor, CaptionItems()), interactive, text is not null)
                with { Key = "ctl:cc", OnRealized = h => ccAnchor.Value = h });
        if (measured && !compact && (commands & MediaCommandFlags.SelectAudioTrack) != 0 && Player.Tracks.Audio.Count > 1)
            controls.Add(TextButton(audio?.Language ?? audio?.Label ?? MediaStrings.AudioTrack,
                () => OpenPicker(audioAnchor, AudioItems()), interactive)
                with { Key = "ctl:audio", OnRealized = h => audioAnchor.Value = h });
        if (PictureInPictureRequested is { } pip && (commands & MediaCommandFlags.PictureInPicture) != 0)
            controls.Add(IconButton(Icons.OpenInNewWindow, pip, interactive) with { Key = "ctl:pip" });
        // The button raises the SAME context request as right-click / long-press / Menu-key. ContextMenu.Attach reads
        // args.Source at invoke time, so placement follows this live button without a captured realization handle.
        controls.Add(IconButton(Icons.More, static () => { }, interactive) with { Key = "ctl:more", OnClick = null, ClickRequestsContext = interactive });
        // LAST, at the extreme corner. See the cluster note above.
        controls.Add(IconButton(presentingFullscreen ? Icons.BackToWindow : Icons.FullScreen, toggleFullscreen, interactive) with { Key = "ctl:fullscreen" });

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
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
            Shrink = 1f, MinWidth = 0f, ClipToBounds = true,   // yields to the card; never widens the element
            Children = controls.ToArray(),
        });

        return new BoxEl
        {
            Direction = 1,
            Gap = 2f,
            Shrink = 1f, MinWidth = 0f,
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
            //
            // OpaqueSurface: every menu this transport raises — quality, speed, captions, audio track — is anchored to
            // a button that sits ON the video, so the plate is over the hole BY CONSTRUCTION. A hole is a DestOut erase
            // and the video is a sibling DComp visual below the UI swapchain, so there is nothing there for an acrylic
            // layer to blur: it would composite premultiplied zero and leave the menu as floating text. Declaring it
            // here instead of letting OverlayHost discover it geometrically makes the plate solid from frame ONE — no
            // query, no frame of latency, and no per-frame slice re-record from the answer flipping.
            m = overlayService.Open(() => anchor.Value,
                () => MenuFlyout.Build(items, () => m?.Close()), FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss)
                { ConstrainToRootBounds = false, OpaqueSurface = true });
            _vis?.SetMenuOpen(true, Now());   // the menu hold, immediately — the epoch effect confirms it and clears it on close
            Sync();
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
        _vis?.Activity(ChromeActivity.Keyboard, Now());   // a handled key reveals with the long keyboard dwell
        Sync();
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
        public bool ShowStatusOverlay { get; init; } = true;

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
                        ShowStatusOverlay = ShowStatusOverlay,
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

    /// <summary>The video hole's letterbox insets for an area — the ONE definition of that geometry, shared by the
    /// render-time terminal (<see cref="Render"/>'s <c>Margin</c>) and the same-solve corrector
    /// (<see cref="SyncHoleLetterbox"/>), so the two roads can never compute different rects. Audio-only (and a
    /// not-yet-laid-out area) means no fit at all: the hole is inert and sits on the whole area.</summary>
    internal static Edges4 HoleInsets(RectF area, SizeI natural, VideoAspectMode mode, double customAspect, float scale)
        => area.W <= 0f || IsAudioOnly(natural)
            ? default
            : LetterboxInsets(area, FitVideoRectSnapped(area, natural, mode, customAspect, scale));

    /// <summary>The pump's overflow safety net, as pure geometry: fit <paramref name="videoRect"/> into its
    /// intersection with <paramref name="viewport"/> with ONE scale for both axes, centred on that intersection. The
    /// presenter scales the frame into whatever rect it is handed, PER AXIS, so an anisotropic clamp IS a distorted
    /// picture — the old per-axis intersection turned a one-axis overflow into a stretched frame with no letterboxing.
    /// Scaling uniformly can only ever produce a SMALLER, correctly-proportioned video, which is what the net is for. A
    /// fully clipped-away rect degrades to the empty intersection exactly as before: there is no aspect to preserve in
    /// a zero-area rect, and the caller's pump/session semantics stay unchanged.</summary>
    internal static RectF ClampUniformToViewport(RectF videoRect, RectF viewport)
    {
        float ix = MathF.Max(videoRect.X, viewport.X), iy = MathF.Max(videoRect.Y, viewport.Y);
        float iw = MathF.Min(videoRect.X + videoRect.W, viewport.X + viewport.W) - ix;
        float ih = MathF.Min(videoRect.Y + videoRect.H, viewport.Y + viewport.H) - iy;
        if (videoRect.W <= 0f || videoRect.H <= 0f || iw <= 0.5f || ih <= 0.5f)
            return new RectF(ix, iy, MathF.Max(0f, iw), MathF.Max(0f, ih));
        float k = MathF.Min(iw / videoRect.W, ih / videoRect.H);   // ONE scale, both axes => the aspect survives
        if (k >= 1f) return videoRect;                             // already inside: nothing to clamp
        float w = videoRect.W * k, h = videoRect.H * k;
        return new RectF(ix + (iw - w) * 0.5f, iy + (ih - h) * 0.5f, w, h);
    }

    /// <summary>The clamp decision for the overflow safety net, as pure geometry: true when <paramref name="videoRect"/>
    /// pokes out of its DIRECT host's laid-out rect by more than half a pixel on any side (the layout-defect case the net
    /// exists for). A clipping ancestor further up (a scroller, the window edge) is deliberately not an input: cutting a
    /// correctly laid-out video is the viewport's job and must not shrink it away from its hole.</summary>
    internal static bool ExceedsDirectHost(RectF videoRect, RectF host)
        => videoRect.X < host.X - 0.5f || videoRect.Y < host.Y - 0.5f
        || videoRect.X + videoRect.W > host.X + host.W + 0.5f
        || videoRect.Y + videoRect.H > host.Y + host.H + 0.5f;

    /// <summary>The video rect (window space) a hole's letterbox <paramref name="insets"/> describe for an area whose
    /// window-space rect is <paramref name="area"/> and whose LAYOUT bounds are <paramref name="layoutArea"/>. The insets
    /// are layout units; under a scaled ancestor the window-space area is bigger or smaller than its layout bounds, so each
    /// inset is scaled by that ratio (exactly 1 on a translation-only chain, so nothing moves there).</summary>
    internal static RectF RectFromHoleInsets(RectF area, RectF layoutArea, Edges4 insets)
    {
        float sx = layoutArea.W > 0f ? area.W / layoutArea.W : 1f;
        float sy = layoutArea.H > 0f ? area.H / layoutArea.H : 1f;
        float l = insets.Left * sx, t = insets.Top * sy, r = insets.Right * sx, bt = insets.Bottom * sy;
        return new RectF(area.X + l, area.Y + t, area.W - l - r, area.H - t - bt);
    }

    /// <summary>The per-edge letterbox insets (DIP) that place <paramref name="video"/> inside <paramref name="area"/> —
    /// the ONE piece of geometry the video stage is built from. The hole child is laid out with exactly these as its
    /// Margin, so its arranged rect IS the fitted video rect and <see cref="PumpNow"/> can place the presenter from it.
    /// Signed: a <see cref="VideoAspectMode.UniformToFill"/> crop or an oversized <see cref="VideoAspectMode.Native"/>
    /// frame yields NEGATIVE insets (the fitted rect overflows the stage and is clipped). Sub-half-pixel insets
    /// collapse to zero — a &lt;0.5px bar was never a bar (the same threshold <see cref="CalculateLetterboxBars"/> has
    /// always used).</summary>
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

    /// <summary>The chrome's view of playback: only USER-VISIBLE stops reveal and hold. Opening / Buffering / Stalled /
    /// Ready with play intent is the stream on its way — the protected session maps Licensed+Buffering onto
    /// <see cref="PlaybackState.Buffering"/> and an ABR switch reports buffering too, and neither may pop the controls up.
    /// Intent wins: a pause request is a stop before the state lands. A zero NaturalSize counts as audio-only only once
    /// actually Playing, so a video whose size is not yet known during Opening never pins the chrome.</summary>
    internal static ChromePlayback ChromePlaybackOf(bool playIntent, PlaybackState state, bool audioOnly) => state switch
    {
        PlaybackState.Failed => ChromePlayback.Failed,
        PlaybackState.Ended => ChromePlayback.Ended,
        PlaybackState.Paused => ChromePlayback.Paused,
        _ when !playIntent => ChromePlayback.Paused,
        PlaybackState.Playing when audioOnly => ChromePlayback.AudioOnly,
        _ => ChromePlayback.Playing,
    };

    /// <summary>Below <c>CompactTransportWidth</c> the chips fold into the ⋯ menu. Unknown width (0, the first layout
    /// pass) is NOT compact and NOT wide — callers wait for a measure rather than rendering chips that vanish.</summary>
    internal static bool IsCompactTransport(float width) => width > 0f && width < CompactTransportWidth;

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static Element BufferingOverlay(BufferingReason bufferingReason, double percent)
    {
        string reason = bufferingReason switch
        {
            BufferingReason.Seeking => MediaStrings.Seeking,
            BufferingReason.QualitySwitch => MediaStrings.ChangingQuality,
            BufferingReason.TrackSwitch => MediaStrings.ChangingTrack,
            BufferingReason.LiveCatchUp => MediaStrings.CatchingUp,
            BufferingReason.NetworkRecovery => MediaStrings.Reconnecting,
            BufferingReason.Rebuffering => MediaStrings.Buffering,
            _ => MediaStrings.Loading,
        };
        Element ring = percent is >= 0 and <= 1
            ? ProgressRing.Determinate((float)percent, 36f)
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

    // ── status overlay: the pure choice, the leaf that shows the live percent, and the memo key ──────────────────────

    /// <summary>Which status overlay (if any) the element mounts over the picture.</summary>
    internal enum StatusOverlayKind : byte { None, Failed, Opening, Buffering }

    /// <summary>What <see cref="Render"/> needs of <see cref="IMediaPlayer.Buffering"/>: WHETHER a rebuffer is in progress
    /// and WHY, never how far along it is. Equal values cut the memo's notification off, so the per-segment percent
    /// churn of a protected open never reaches the element.</summary>
    internal readonly record struct BufferingKey(bool IsBuffering, BufferingReason Reason);

    internal static BufferingKey BufferingKeyOf(BufferingInfo info) => new(info.IsBuffering, info.Reason);

    /// <summary>The overlay choice as pure logic. A decorative element or a host that owns its loading visuals
    /// (<paramref name="hostOwnsVisuals"/>) has none at all. A terminal failure wins over everything. A start in
    /// progress shows the opening ladder (nothing for the first 500 ms, <paramref name="startupPhase"/> 0). Otherwise a
    /// rebuffer earns the pill only once it has outlived its own 500 ms delay, except at the handoff from a start whose
    /// ring is already up (<paramref name="startupPhase"/> at least 1): the pill replaces the ring at once.</summary>
    internal static StatusOverlayKind ChooseStatusOverlay(bool decorative, bool hostOwnsVisuals, PlaybackState state,
        bool startingUp, int startupPhase, bool bufferingWanted, bool bufferingDelayElapsed)
    {
        if (decorative || hostOwnsVisuals) return StatusOverlayKind.None;
        if (state == PlaybackState.Failed) return StatusOverlayKind.Failed;
        if (startingUp) return startupPhase == 0 ? StatusOverlayKind.None : StatusOverlayKind.Opening;
        return bufferingWanted && (bufferingDelayElapsed || startupPhase >= 1) ? StatusOverlayKind.Buffering : StatusOverlayKind.None;
    }

    /// <summary>Should a mid-play buffering state start the rebuffer overlay's delay clock at all. Never during a start
    /// (the opening ladder owns it). A SEEK or an ABR QUALITY SWITCH behind a picture that is already presented is
    /// silent: the user asked for it or never asked at all, the picture is up, and the seek bar's inline spinner
    /// covers a slow seek; a full-surface pill flashing over the frame on every J/L/arrow press (or scrub preview)
    /// contradicts <see cref="MediaSeekBar"/>'s own contract. A real stall or a network rebuffer is not silent.</summary>
    internal static bool BufferingOverlayWanted(bool startingUp, bool isBuffering, BufferingReason reason,
        PlaybackState state, bool framePresented)
    {
        if (startingUp) return false;
        if (!isBuffering && state is not (PlaybackState.Buffering or PlaybackState.Stalled)) return false;
        return !(framePresented && (reason is BufferingReason.Seeking or BufferingReason.QualitySwitch));
    }

    /// <summary>The percent an overlay ring shows, in 5% steps (20 notches is as fine as a 36-40 DIP ring can show), or
    /// -1 (indeterminate) when the backend publishes none. Quantising is what lets the leaf's memo cut off the
    /// per-segment ticks: a ring that moved on every appended segment re-rendered for no visible change.</summary>
    internal static double QuantizeStatusPercent(double percent)
        => percent is >= 0 and <= 1 ? Math.Round(percent * 20.0) / 20.0 : -1.0;

    /// <summary>The leaf's re-pushed inputs (an immutable record, so an unchanged re-push is coalesced).</summary>
    private sealed record StatusProps(StatusOverlayKind Kind, bool PlayIntent, BufferingReason Reason, bool Determinate);

    /// <summary>The opening / rebuffer overlay as its OWN component: the only subscriber of the live buffering percent.
    /// The element decides that an overlay exists and why; this leaf reads how far along it is (quantised, behind a memo),
    /// so a buffered-amount tick re-renders this small subtree and never the whole player element.</summary>
    private sealed class MediaStatusDetail : Component
    {
        public required IMediaPlayer Player { get; init; }

        public override Element Render()
        {
            var props = UseProps<StatusProps>();
            var percent = UseComputed(() => QuantizeStatusPercent(Player.Buffering.Value.Percent));
            if (props.Kind == StatusOverlayKind.Opening)
                return OpeningOverlay(props.PlayIntent, props.Determinate ? percent.Value : -1.0);
            return BufferingOverlay(props.Reason, percent.Value);
        }
    }

    // ── pure helpers (unit-tested) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Degrade decision: audio-only (no hole-punch) iff the source has no video.</summary>
    internal static bool IsAudioOnly(SizeI natural) => natural.IsEmpty;

    /// <summary>The element's "video is past Opening" test: the source has video and the player has left Idle/Opening.
    /// Shared by <see cref="Render"/> and the caption leaf so the two can never disagree about when captions may show.</summary>
    internal static bool IsVideoReady(SizeI natural, PlaybackState state)
        => !IsAudioOnly(natural) && state is not (PlaybackState.Idle or PlaybackState.Opening);

    /// <summary>Fit a <paramref name="natural"/>-sized frame into <paramref name="area"/> (DIP) per <paramref name="stretch"/>.
    /// Returns the placed video rect (DIP), centered. <see cref="MediaStretch.UniformToFill"/> and
    /// <see cref="MediaStretch.None"/> (native 1:1) both deliberately OVERFLOW the area when the frame is larger —
    /// the caller's viewport clip crops the excess (true center-crop, no distortion); the other modes fit within it.</summary>
    internal static RectF FitVideoRect(RectF area, SizeI natural, MediaStretch stretch)
        => FitVideoRect(area, natural, ToAspectMode(stretch), 16.0 / 9.0);

    internal static RectF FitVideoRect(RectF area, SizeI natural, VideoAspectMode aspectMode, double customAspect,
        float scale = 1f)
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
                // natural is PIXELS, area is DIP — "native size" means 1 video px per DEVICE px, so convert first.
                // Without this the Native fit rendered scale× too large on any high-DPI display (px used as DIP).
                // TRUE 1:1: no per-axis clamp to the area. A frame larger than the area overflows and the viewport
                // clip crops it — the same road UniformToFill already takes — and a smaller frame is centred with a
                // border. Either way the picture is never distorted (both axes share the one `inv` scale). Uses
                // `natural`, not the Custom-rewritten vw/vh, because Native ignores the custom-aspect rewrite above.
                float inv = scale > 0f ? 1f / scale : 1f;
                return Center(area, natural.Width * inv, natural.Height * inv);
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

    /// <summary><see cref="FitVideoRect(RectF, SizeI, VideoAspectMode, double, float)"/> on the DEVICE pixel grid at
    /// <paramref name="scale"/>: the hole's edges are the registry's whole-pixel video rect, so the UI hole and the
    /// composited video share a boundary at every fractional scale (see <see cref="SnapVideoRect"/>).</summary>
    internal static RectF FitVideoRectSnapped(RectF area, SizeI natural, VideoAspectMode aspectMode, double customAspect,
        float scale)
    {
        RectF fit = FitVideoRect(area, natural, aspectMode, customAspect, scale);
        return area.W <= 0f || area.H <= 0f || natural.IsEmpty ? fit : SnapVideoRect(fit, scale);
    }

    /// <summary>Snap a DIP rect so its DEVICE edges are whole pixels: convert to device px at <paramref name="scale"/>, apply
    /// <see cref="VideoSurfaceRegistry.SnapToDevicePixels"/> (rule R, the SAME rule the registry applies to the placed rect
    /// and the composite applies to the hole-erase rect), and convert back. A rect whose device edges are already whole
    /// pixels (scale 1 with integral DIP edges, say) comes back unchanged.</summary>
    internal static RectF SnapVideoRect(RectF dip, float scale)
    {
        float s = scale <= 0f ? 1f : scale;
        RectF dev = VideoSurfaceRegistry.SnapToDevicePixels(ToDeviceRect(dip, s));
        return new RectF(dev.X / s, dev.Y / s, dev.W / s, dev.H / s);
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

    // ── the surface-presented report (a host's make-before-break hand-off between two presenters) ───────────────────

    /// <summary>Raised ONCE on the UI thread, from the pump, when THIS element's own composited surface is bound
    /// (<see cref="VideoBinding.Bound"/>) and visible: the host created its
    /// video child visual and bound the player's handle to it (the registry's per-token
    /// <see cref="VideoBinding.Surface"/> turned non-none and <see cref="VideoBinding.Bound"/> turned true, a bind that can
    /// fail and retry), while the element is active, placed and showing (the session publishes a surface). A host that moves a
    /// player from one presenter to another (the main window to a pop-out window) keeps the outgoing presenter mounted
    /// until the incoming one raises this, so no frame is left with no video composited anywhere. Never raised for an
    /// audio-only stream; frozen at mount like every init prop.</summary>
    public Action? SurfacePresented { get; init; }

    private bool _surfacePresentedRaised;
    private int _surfaceProbePumps;
    private const int MaxSurfaceProbePumps = 120;   // about two seconds of frames; past it only a native pump re-checks

    /// <summary>Pump-side half of <see cref="SurfacePresented"/>. The presenter drain that creates the child visual runs on
    /// the render thread AFTER the pump that bound the handle and wakes nobody, and the surface signal is written from
    /// that thread, so it is only ever PEEKED here (a 4-byte id, never subscribed): the report waits for the surface AND
    /// the slot's bound flag AND the session's own readiness, the same conditions the element ANDs into its visibility.
    /// Until all are observed, one follow-up pump per frame re-checks them for a bounded number of pumps.</summary>
    private void NoteSurfacePresented(in VideoBinding b)
    {
        if (SurfacePresented is not { } report || _surfacePresentedRaised) return;
        if (!b.Surface.Peek().IsNone && b.Bound.Peek() && !Player.VideoSurface.Peek().IsNone)
        {
            _surfacePresentedRaised = true;
            report();
            return;
        }
        if (++_surfaceProbePumps <= MaxSurfaceProbePumps) b.RequestPump();
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
