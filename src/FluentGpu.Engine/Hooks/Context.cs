using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace FluentGpu.Hooks;

/// <summary>A context channel with a default. Consumers read the nearest provider's value via <c>UseContext</c>.</summary>
public sealed class Context<T>
{
    public T Default;
    public Context(T defaultValue) => Default = defaultValue;
}

/// <summary>
/// The ambient client size (DIP), pushed by the host each frame so components can adapt to available width
/// (responsive layout / NavigationView display modes). Read with <c>UseContext(Viewport.Size)</c>.
/// </summary>
public static class Viewport
{
    public static readonly Context<Size2> Size = new(default);
    /// <summary>The window DIP→device-px scale factor (1.0 at 96 DPI). Published by the host; updated on a DPI change.
    /// A component that must size a device-pixel resource (e.g. <c>MediaPlayerElement</c> sizing the MF video stream to
    /// its laid-out rect) reads <c>UseContext(Viewport.Scale)</c>. Default 1.0 (headless / pre-publish).</summary>
    public static readonly Context<float> Scale = new(1f);
    /// <summary>The app-zoom factor (browser-style Ctrl+= zoom; steps on the <see cref="FluentGpu.Foundation.ZoomLadder"/>).
    /// <see cref="Scale"/> already CONTAINS it (Scale = OS DPI scale × Zoom), so every DIP↔device-px conversion keeps
    /// using Scale — read this channel only to DISPLAY the level (a settings row, a diagnostics receipt), never to
    /// convert coordinates. Default 1.0 (headless / pre-publish).</summary>
    public static readonly Context<float> Zoom = new(1f);
}

/// <summary>
/// A per-frame tick the host bumps (only while something subscribes, so it's free when idle). A tree-level concern that
/// must poll each frame — e.g. an overlay driving a timed close animation to completion — reads
/// <c>UseContext(FrameClock.Tick)</c> to re-render every frame for as long as it's mounted.
/// </summary>
public static class FrameClock
{
    public static readonly Context<long> Tick = new(0L);

    /// <summary>The <see cref="Tick"/> twin the adaptive GPU governor MAY pace. A <see cref="Tick"/> subscriber is never
    /// paced (it is the smooth playhead, a drag dwell — latency the user feels); a continuous visual that only has to look
    /// smooth (a full-screen visualizer) subscribes here instead, so a GPU that cannot hold the panel rate drops THAT
    /// motion to the governor's steady cadence rather than thrashing into vblank misses. <c>AppHost.PowerCapFps</c> caps
    /// both. Same value as <see cref="Tick"/> on every frame it is published.</summary>
    public static readonly Context<long> PaceableTick = new(0L);

    /// <summary>This frame's lattice-snapped "now" in QPC ticks (<see cref="System.Diagnostics.Stopwatch.Frequency"/>
    /// units — the <c>Stopwatch.GetTimestamp()</c> domain): the host's <see cref="FluentGpu.Pal.FrameClock.FrameQpc"/>,
    /// the same target time the scroll frame step and DirectManipulation consume. Monotone frame to frame within one host
    /// (never rewinds; see the multi-window note below for the cross-host caveat).
    /// <para>Set by the host at the very top of <c>AppHost.RunFrame</c> — before the input pump, cross-thread posts,
    /// timers, the <see cref="Tick"/> publish and the reactive flush — so every handler, effect, render and bind thunk
    /// that runs inside a frame reads THIS frame's value. <c>0</c> before the first frame. Headless
    /// (<c>RefreshLattice.Headless</c>) it is the deterministic accumulated frame time instead of a QPC read, so it
    /// starts at 0 on the first frame and advances by the fixed step.</para>
    /// <para>UI-thread value (a plain static, not a signal: reading it subscribes nothing — pair it with a
    /// <see cref="Tick"/> subscription or an animation-driven re-render to be re-evaluated each frame).</para>
    /// <para>Multi-window: every host on the UI thread (the main window and each detached child window, ticked one
    /// after another on the same thread) writes it at the top of its OWN <c>RunFrame</c>, so it is last-writer: inside
    /// a host's frame it is that host's clock (each host snaps to its own display lattice and frame latency, so from
    /// one host's frame to the next host's the value may differ, even step back; code outside any frame sees whichever
    /// host ran last). A repaint that bypasses <c>RunFrame</c> (the OS modal move/size keep-alive <c>Paint</c>) does
    /// not rebuild the clock and keeps the last frame's value.</para></summary>
    public static long FrameQpc { get; internal set; }

    /// <summary>The PREDICTED vblank this frame's pixels land on, in QPC ticks (<see cref="System.Diagnostics.Stopwatch.Frequency"/>
    /// units): the host's <see cref="FluentGpu.Pal.FrameClock.PresentQpc"/> (lattice-snapped frame time + the
    /// swap-chain's frame-latency lead). <b>This is the time app motion should sample</b> — a karaoke wipe, a progress
    /// sweep, any position-from-time value — so what is drawn is where the motion will be when the frame is actually
    /// seen, on a vsync-regular lattice. (Flutter hands every Ticker the frame's vsync timestamp,
    /// <c>SchedulerBinding.currentFrameTimeStamp</c>; Chromium animates on <c>BeginFrameArgs.frame_time</c>.) Never
    /// sample <c>Environment.TickCount64</c> for motion — its ~15.6 ms quanta step visibly at display rate.
    /// <para>Seconds: <c>FrameClock.PresentQpc / (double)Stopwatch.Frequency</c>. Same lifecycle as
    /// <see cref="FrameQpc"/>: set at the top of <c>AppHost.RunFrame</c> on every path (headless included —
    /// deterministic there: frame time + one refresh period), <c>0</c> before the first frame, UI-thread,
    /// last-writer across multiple hosts.</para></summary>
    public static long PresentQpc { get; internal set; }
}

/// <summary>
/// The app-wide window-visibility signal, published by the host: <c>false</c> while the window is minimized (or while
/// the app has signalled a power suspend via <c>AppHost.SetWindowActive(false)</c>), <c>true</c> otherwise. The
/// <see cref="RenderContext.UseIsActive"/> hook AND-folds it with the component's own KeepAlive-parked state so a
/// component learns it is inactive when its page is backgrounded OR the window is invisible. Published as a host-owned
/// ambient (like <c>Viewport.Size</c>); the channel value is the visibility <c>IReadSignal&lt;bool&gt;</c> itself
/// (which never re-publishes, so resolving it is re-render-free — the hook reads the inner signal to subscribe).
/// NAMESPACED <c>Activation.IsActive</c> (bare <c>IsActive</c> collides with <c>DragController.IsActive</c>).
/// </summary>
public static class Activation
{
    public static readonly Context<IReadSignal<bool>?> IsActive = new(null);
}

/// <summary>
/// The host-owned <c>VideoSurfaceRegistry</c> — the portable arbitration buffer a component's <c>UseVideoSurface</c>
/// hook (or a media player) writes video-surface intents into, drained into the render-thread <c>IVideoPresenter</c> at
/// phase 11. Read with <c>UseContext(VideoCompositor.Current)</c>. Null (headless / a non-composited window) means video
/// compositing is unavailable — the hook returns an inert binding and the page shows its poster/fallback.
/// </summary>
public static class VideoCompositor
{
    public static readonly Context<FluentGpu.Media.VideoSurfaceRegistry?> Current = new(null);
}

/// <summary>
/// A host-owned registration surface a tree-level concern can hook into without the host depending on it. The host
/// bridges <see cref="KeyPreview"/> into the input dispatcher's pre-focus key hook, and runs
/// <see cref="AfterAnimations"/> after the animation engine ticks but before recording. Read the host instance via
/// <c>UseContext(InputHooks.Current)</c>.
/// </summary>
/// <summary>Parameters for a DETACHED video window (the pop-out mini-player). <see cref="Content"/> is a component the
/// host mounts as the window's root; the window is composited, movable/resizable, and (by default) always-on-top.</summary>
/// <param name="InitialBoundsPx">Optional restored placement (outer rect, physical virtual-screen px). Empty ⇒ the host
/// picks a picture-in-picture home at the bottom-right of the parent's monitor. A restored rect is still clamped to a
/// visible monitor, so a window remembered on a display that is now gone still opens where the user can see it.</param>
/// <param name="MinClientSizeDip">Optional minimum client size (DIP). Empty ⇒ the host's 16:9-ish default floor.</param>
public readonly record struct DetachedWindowRequest(
    string Title, FluentGpu.Foundation.Size2 InitialSizeDip, Component Content, bool AlwaysOnTop = true,
    FluentGpu.Foundation.RectF InitialBoundsPx = default, FluentGpu.Foundation.Size2 MinClientSizeDip = default);

/// <summary>Where a pop-out open spent its time, split so a slow open names its stage (F110). The window is created hidden and
/// revealed once its first frame has presented, so the four stages are separate costs and only the first two are paid
/// synchronously inside the open call. All milliseconds; <see cref="FirstFrameMs"/> and <see cref="FirstPresentMs"/> are
/// known only once the window was revealed (see <see cref="IDetachedVideoWindow.OnRevealed"/>), and are 0 before.
/// <list type="bullet">
/// <item><see cref="WindowCreateMs"/>: native window creation and placement (everything up to the host constructor).</item>
/// <item><see cref="HostCtorMs"/>: the child host constructor (swapchain creation, scene and reconciler setup, mount).</item>
/// <item><see cref="FirstFrameMs"/>: the child's first <c>RunFrame</c> (the full reconcile, layout and record).</item>
/// <item><see cref="FirstPresentMs"/>: wall time from the start of the open until the first present was observed on the UI
/// thread (it contains the three stages above plus any render-thread wait).</item>
/// </list>
/// <see cref="TimedOut"/> is true when the window was shown by the reveal timeout instead of by a presented frame.
/// <see cref="RenderPresentMs"/> (F215) is the same stage as <see cref="FirstPresentMs"/> read from the clock the RENDER thread
/// stamped when the child's first present succeeded, so <c>FirstPresentMs - RenderPresentMs</c> is the lag the UI added in
/// noticing it; -1 when the reveal came from the timeout (no present was seen) or the backend does not stamp it. The time to the
/// child's first VIDEO BIND is a later stage than the reveal: <see cref="IDetachedVideoWindow.OnFirstVideoBound"/>.</summary>
public readonly record struct DetachedOpenTiming(
    double WindowCreateMs, double HostCtorMs, double FirstFrameMs, double FirstPresentMs, bool TimedOut, double RenderPresentMs = -1.0);

/// <summary>A live handle to a detached video window (see <see cref="InputHooks.OpenDetachedWindow"/>).</summary>
public interface IDetachedVideoWindow
{
    /// <summary>The open-cost split. Only <see cref="DetachedOpenTiming.WindowCreateMs"/> and
    /// <see cref="DetachedOpenTiming.HostCtorMs"/> are filled while the window is still hidden; the rest arrives with
    /// <see cref="OnRevealed"/>. Default: all zero (a backend that does not measure).</summary>
    DetachedOpenTiming OpenTiming => default;
    /// <summary>Fired once, on the UI thread, when the window has been revealed (shown after its first present, or by the reveal
    /// timeout), with the full <see cref="DetachedOpenTiming"/>. Set it right after the open call returns: the reveal happens on
    /// a later frame. Default: ignored.</summary>
    Action<DetachedOpenTiming>? OnRevealed { get => null; set { } }
    /// <summary>Milliseconds from the start of the open to the pop-out's first SUCCESSFUL video bind (the presenter accepted a
    /// swap-chain handle for one of the window's surfaces: the first moment the picture can be composited there), or -1 while none
    /// has landed. The reveal (<see cref="OnRevealed"/>) only proves the child presented its own frame; with a protected source the
    /// bind follows the native handle and can land later. Default: -1 (a backend that does not measure).</summary>
    double FirstVideoBindMs => -1.0;
    /// <summary>Fired once, on the UI thread, when <see cref="FirstVideoBindMs"/> is known (its argument). Set it right after the open
    /// call returns. Default: ignored.</summary>
    Action<double>? OnFirstVideoBound { get => null; set { } }
    /// <summary>True until the window is closed/reaped.</summary>
    bool IsOpen { get; }
    /// <summary>Toggle persistent always-on-top.</summary>
    void SetTopmost(bool topmost);
    /// <summary>Move/resize the window (outer rect, physical virtual-screen px).</summary>
    void SetBounds(FluentGpu.Foundation.RectF outerBoundsPx);
    /// <summary>Move the window's outer origin WITHOUT touching its size (<see cref="Pal.IPlatformWindow.MoveToPx"/>) —
    /// restore a remembered position independent of whatever size the window happens to be at right now. Default:
    /// <see cref="SetBounds"/> with the CURRENT size (<see cref="BoundsPx"/>), for a backend whose window has no
    /// standalone move primitive; a backend with one (Win32) overrides to call it directly and skip the read-back.</summary>
    void MoveTo(FluentGpu.Foundation.Point2 outerOriginPx)
        => SetBounds(new FluentGpu.Foundation.RectF(outerOriginPx.X, outerOriginPx.Y, BoundsPx.W, BoundsPx.H));
    /// <summary>Close the window (the pop-out docks back to inline).</summary>
    void Close();
    /// <summary>Fired exactly once, on the UI thread, when the window has closed (OS chrome/Alt+F4 OR programmatic
    /// <see cref="Close"/>), immediately before the child host is torn down. Cleared after it fires.</summary>
    Action? OnClosed { get; set; }

    /// <summary>Where the window is NOW (outer rect, physical virtual-screen px), or an empty rect when the backend
    /// cannot report it. The read side of <see cref="SetBounds"/> — an owner that wants to reopen the window where the
    /// user left it has to be able to ask.</summary>
    FluentGpu.Foundation.RectF BoundsPx => default;
    /// <summary>Retitle a live window (the OS taskbar / Alt+Tab text), so a title that describes CONTENT can follow the
    /// content instead of being frozen at whatever was playing when the window opened.</summary>
    void SetTitle(string title) { }
    /// <summary>Fired on the UI thread when the user has finished moving or resizing the window (settled bounds, outer
    /// rect, physical px). Debounced by the host — one call per gesture, not one per pixel — so an owner can persist
    /// geometry directly from it.</summary>
    Action<FluentGpu.Foundation.RectF>? BoundsChanged { get; set; }

    /// <summary>Borderless-fullscreen THIS window, on the monitor it is currently sitting on.
    /// <para>That last clause is the whole reason this member exists rather than the owner reusing
    /// <c>InputHooks.WindowSetFullscreen</c>. A detached video window is routinely dragged onto a DIFFERENT display from
    /// the app that opened it — that is most of the point of popping it out — and <c>WindowSetFullscreen</c> is bound to
    /// the OWNER window, whose backend resolves the target monitor from the OWNER's HWND. Fullscreening through it would
    /// therefore yank the picture back to the app's monitor, which reads as the video "jumping screens" on a keypress.
    /// Routed here it lands on the child window's own HWND, so the existing
    /// <c>MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)</c> in the backend picks the right display for free — no
    /// monitor plumbing anywhere in the seam.</para>
    /// <para>Fullscreen here is a MODE OF THIS WINDOW, orthogonal to whatever placement model the owner runs: the window
    /// stays the same window, keeps its content mounted, and returns to its previous rect on <c>false</c>. Defaulted to a
    /// no-op so headless and any backend without borderless fullscreen are unaffected.</para></summary>
    void SetFullscreen(bool fullscreen) { }

    /// <summary>Whether this window is presenting borderless-fullscreen right now — the read side of
    /// <see cref="SetFullscreen"/>. False for a closed window and for any implementation that cannot do it (the default),
    /// so a caller that asks never has to special-case "unsupported" separately from "not fullscreen".</summary>
    bool IsFullscreen => false;

    /// <summary>True once this window's own render frame has thrown a non-device-loss exception (INCIDENT 2026-09: a
    /// child's Close/submit fault used to kill the whole process — <c>AppHost.SubmitPresentOnRenderThread</c>'s catch
    /// now latches this instead of rethrowing). A one-way latch: once true, this window is no longer presenting
    /// anything and never will again. Default false so a backend without the concept (or a handle backed by a host
    /// that predates this member) reads as healthy forever, matching prior behavior.</summary>
    bool RenderFailed => false;

    /// <summary>F110: true while the window is PARKED warm (<see cref="Park"/>): hidden, its host stopped producing, its content
    /// still mounted and its swapchain still alive, waiting for <see cref="Unpark"/> (or <see cref="Close"/>). A parked window is
    /// still <see cref="IsOpen"/>. Default false (a backend without warm reuse never parks).</summary>
    bool IsParked => false;

    /// <summary>F110: hide the window and PARK its host instead of closing it (Win32 <c>SW_HIDE</c>: no frames are produced, no
    /// present is made; the child host, its mounted tree and its swapchain stay alive), so the next open can reuse it with
    /// <see cref="Unpark"/> and skip the window, swapchain and tree construction. A fullscreen window leaves fullscreen first,
    /// so it comes back windowed at the rect the user last chose, and a pending settled-bounds change is delivered
    /// (<see cref="BoundsChanged"/>) before it hides. Returns false when the window cannot be parked (still waiting for its
    /// reveal, its render path failed, closed, or the backend has no warm reuse): the caller closes it instead. Idempotent
    /// while parked. UI thread. Closing a parked window (<see cref="Close"/>) disposes it normally; the owner decides when.</summary>
    bool Park() => false;

    /// <summary>F110: bring a parked window back for a new open. Applies the request's restored bounds (clamped into a visible
    /// monitor's work area), always-on-top state and title, then re-arms the reveal gate: the window is shown on the next child
    /// frame (<see cref="OnRevealed"/> fires then, so set it after this call returns, as after an open), and the host resumes
    /// producing. The request's <see cref="DetachedWindowRequest.Content"/> is ignored: the parked tree is reused, so it must
    /// follow live signals rather than capture values. Returns false when the window is not parked or cannot be reused (the
    /// caller opens a new one). UI thread.</summary>
    bool Unpark(DetachedWindowRequest request) => false;
    /// <summary>Fired once, on the render thread, the instant <see cref="RenderFailed"/> latches. The owner should
    /// treat this like <see cref="OnClosed"/>'s sibling — typically marshal to the UI thread and close the pop-out
    /// (it can no longer present) rather than leave a frozen/blank window around. Default no-op.</summary>
    Action? OnRenderFailed { get; set; }
}

public sealed class InputHooks
{
    /// <summary>Return true to consume the key. Set by an open overlay; cleared when it closes.</summary>
    public Func<int, bool>? KeyPreview;
    public bool Preview(int key) => KeyPreview?.Invoke(key) ?? false;

    /// <summary>App-zoom wheel hook (browser Ctrl+wheel): invoked by the dispatcher for a Ctrl+wheel AFTER element-level
    /// <c>OnPointerWheel</c> handlers declined it and BEFORE the viewport scrolls. The argument is the signed device
    /// notch count (&gt;0 = wheel rotated away from the user = zoom in; <c>WheelClassifier.ZoomNotches</c>); only a
    /// vertical wheel zooms. Return true to consume — the viewport never scrolls that notch. Null (the default) leaves Ctrl+wheel scrolling exactly as before. Note: the Win32 backend
    /// synthesizes pinch-zoom from Ctrl + hi-res/touchpad wheel BEFORE events reach the dispatcher, so this hook only
    /// ever sees detented mouse wheels.</summary>
    public Func<float, bool>? ZoomWheel;

    /// <summary>The active contact's sampled flick velocity (px/s, window space) — host-wired to the dispatcher's
    /// <c>PointerVelocity</c>. A cross-axis swipe control (SwipeControl/FlipView, <c>BoxEl.DragYieldsToPan</c>) reads it
    /// from its <c>OnClick</c> release/commit edge to make the WinUI velocity snap (100px open / 31px/s close;
    /// flick-navigate) instead of a fixed-duration substitute. Zero between gestures / on a mouse / 0-stamp stream.</summary>
    public Func<Point2>? PointerVelocity;

    /// <summary>The currently-focused node (host-wired to the dispatcher). An opening overlay captures it so the focus
    /// can be restored when the overlay closes (WinUI flyout focus-restoration).</summary>
    public Func<NodeHandle>? GetFocus;
    /// <summary>Restore focus to a node (host-wired to the dispatcher). Used by an overlay on close.</summary>
    public Action<NodeHandle>? RestoreFocus;

    /// <summary>Move keyboard focus to a node (host-wired to the dispatcher's SetFocus). visual=true draws the
    /// engine focus ring — keyboard-initiated moves (ItemsView arrow nav / typeahead) pass true; pointer-style
    /// moves pass false.</summary>
    public Action<NodeHandle, bool>? FocusNode;

    /// <summary>Move keyboard focus to a node WITH the focus visual (host-wired to the dispatcher's
    /// <c>SetFocus(node, visual: true)</c>) — the roving-focus seam for container controls (RadioButtons arrow
    /// navigation, RadioButtons.cpp MoveFocus), where WinUI shows the focus rect on the newly-focused item.
    /// Contrast <see cref="RestoreFocus"/>, which restores silently (overlay close).</summary>
    public Action<NodeHandle>? MoveFocusVisual;

    /// <summary>Push a dispatcher FOCUS SCOPE rooted at a node: Tab/Shift-Tab cycle inside its subtree until popped
    /// (WinUI TabFocusNavigation=Cycle - the ContentDialog/flyout focus trap). Host-wired to
    /// <c>InputDispatcher.PushFocusScope</c>.</summary>
    public Action<NodeHandle>? PushFocusScope;
    /// <summary>Remove the focus scope previously pushed for this root (order-independent - overlays can close out of
    /// stack order). Host-wired to <c>InputDispatcher.RemoveFocusScope</c>.</summary>
    public Action<NodeHandle>? PopFocusScope;
    /// <summary>First focusable node (tab order) within a subtree - the focus trap's initial-focus query.
    /// Host-wired to <c>InputDispatcher.FirstFocusableIn</c>.</summary>
    public Func<NodeHandle, NodeHandle>? FirstFocusableIn;

    /// <summary>Set by the overlay host: the window lost activation → close every light-dismiss overlay
    /// (WinUI window-deactivation dismiss). Invoked from the dispatcher's WindowBlur via the host wiring.</summary>
    public Action? WindowBlurred;
    public event Action? WindowBlurObserved;
    public event Action<Point2>? PointerDownObserved;
    public event Action? ScrollStartedObserved;
    public void NotifyWindowBlur() { WindowBlurred?.Invoke(); WindowBlurObserved?.Invoke(); }
    public void NotifyPointerDown(Point2 point) => PointerDownObserved?.Invoke(point);
    public void NotifyScrollStarted() => ScrollStartedObserved?.Invoke();

    /// <summary>The last mouse/pen pointer position in window DIP, or null when there is none to trust (cursor outside
    /// the client area / window blurred). Host-wired to <c>InputDispatcher.PointerPosition</c>. The ToolTip safe-zone
    /// poll reads it (WinUI IsToolTipInSafeZone's global pointer test) so the tooltip bubble itself stays
    /// hit-test-invisible — a real tooltip never intercepts pointer or wheel input.</summary>
    public Func<Point2?>? GetPointerPosition;

    /// <summary>Temporarily override the window cursor for one owner. Passing null releases only that owner's override
    /// and immediately restores the cursor resolved from the current hover chain.</summary>
    public Action<object, CursorId?>? SetCursorOverride;

    /// <summary>The OverlayHost scrim's dismiss-and-reopen seam (host-wired to <c>InputDispatcher.RequestContextAt</c>):
    /// a right-click on the light-dismiss scrim closes the top overlay AND re-fires the context request at the same
    /// window point so the node underneath opens its own context menu in ONE gesture (WinUI outside-right-click). The
    /// scrim unmarks its HitTestVisible synchronously first so this hit-tests THROUGH the dying scrim. Null in a
    /// host-less tree ⇒ no redispatch.</summary>
    public Action<Point2>? RedispatchContextAt;

    // ── custom-titlebar chrome seam (host-wired in the AppHost ctor; consumed by the TitleBar control) ───────────────
    /// <summary>Pull the window placement (drives the max↔restore caption glyph). Null = standard frame (Normal).</summary>
    public Func<WindowState>? GetWindowState;
    /// <summary>Pull window activation (drives titlebar dimming). Null = always active.</summary>
    public Func<bool>? IsWindowActive;
    /// <summary>Caption-button commands → <c>IPlatformWindow.Minimize/ToggleMaximize/CloseWindow</c>.</summary>
    public Action? WindowMinimize, WindowToggleMaximize, WindowClose;
    /// <summary>Borderless monitor-fullscreen state + command. Media surfaces use this instead of maximizing.</summary>
    public Func<bool>? IsWindowFullscreen;
    public Action<bool>? WindowSetFullscreen;
    /// <summary>Raised when any OS move/size loop of THIS window BEGINS (<see cref="FluentGpu.Pal.InputKind.WindowMoveSizeBegan"/>)
    /// — a caption-region drag or an edge resize. A consumer that holds chrome for a window move starts the hold here and
    /// releases it on <see cref="WindowMoveSizeEndedObserved"/>. Subscribe with a cached delegate; unsubscribe on unmount.</summary>
    public event Action? WindowMoveSizeBeganObserved;
    public void NotifyWindowMoveSizeBegan() => WindowMoveSizeBeganObserved?.Invoke();
    /// <summary>Raised when any OS move/size loop of THIS window ends (<see cref="FluentGpu.Pal.InputKind.WindowMoveSizeEnded"/>)
    /// — edge resizes included, so a subscriber that did not start one ignores it. Subscribe with a cached delegate;
    /// unsubscribe on unmount.</summary>
    public event Action? WindowMoveSizeEndedObserved;
    public void NotifyWindowMoveSizeEnded() => WindowMoveSizeEndedObserved?.Invoke();
    /// <summary>Open a movable/resizable, always-on-top DETACHED video window hosting the request's content in its OWN
    /// window + scene + swapchain (the pop-out mini-player). Returns a handle, or null when unavailable (a child host,
    /// headless, or a backend without secondary swapchains; the async render path is NOT a reason - the pop-out presents
    /// through the parent's render thread). Host-wired to <c>AppHost.OpenDetachedWindow</c>.</summary>
    public Func<DetachedWindowRequest, IDetachedVideoWindow?>? OpenDetachedWindow;
    /// <summary>Whether <see cref="OpenDetachedWindow"/> would actually succeed right now (a child host, headless, or a
    /// backend without secondary swapchains all say no). An affordance can PREFLIGHT with this instead of discovering the
    /// answer by opening nothing — the difference between "that option isn't available here" and a dead click.</summary>
    public Func<bool>? CanOpenDetachedWindow;
    /// <summary>Push the titlebar drag/button regions (array + count — the caller reuses ONE array across pushes,
    /// the host forwards <c>regions.AsSpan(0, count)</c> to <c>IPlatformWindow.SetTitleBarRegions</c>; push happens
    /// on titlebar relayout only, never per frame).</summary>
    public Action<TitleBarRegion[], int>? SetTitleBarRegions;
    /// <summary>Node → its laid-out absolute rect (window DIP) — host-wired to <c>SceneStore.AbsoluteRect</c>. The
    /// TitleBar control builds its region report from captured part handles in a layout effect.</summary>
    public Func<NodeHandle, RectF>? GetNodeRect;
    /// <summary>Bumped by the host on WindowFocus/WindowBlur/WindowStateChanged: the TitleBar reads it (subscribes)
    /// and pulls <see cref="GetWindowState"/>/<see cref="IsWindowActive"/> for the current values on re-render.</summary>
    public Signal<int>? WindowChromeEpoch;
    /// <summary>True while this window is not being shown — parked (minimized / hidden) or the primary swapchain's
    /// <c>IsOccluded</c>: the cloaked / covered present stand-down, or the DXGI occlusion latch where the backend reports
    /// one (not on composition swapchains). Not merely inactive: alt-tab keeps it false so a second-monitor ambient keeps
    /// running. Published by the host on EVERY frame, painted or idle (<c>AppHost.RunFrame</c>, above the park and idle
    /// gates); while it reads occluded on an un-parked window the host re-probes the target with a forced present every
    /// 250 ms, so a consumer that pauses on it still hears it fall. Null in a host-less tree.</summary>
    public Signal<bool>? WindowOccluded;

    // ── E4 windowed out-of-bounds popups (host-wired in the AppHost ctor; consumed by OverlayHost) ──────────────────
    /// <summary>Window-DIP point → the containing MONITOR's work area translated into window-DIP space (the container
    /// windowed popups clamp against — WinUI FlyoutBase_Partial.cpp:3382-3392 <c>useMonitorBounds</c>). The host owns
    /// the DIP↔screen conversion (<c>IPlatformWindow.ClientOriginPx</c> + <c>IPlatformApp.GetWorkArea</c>).</summary>
    public Func<Point2, RectF>? GetWorkArea;
    /// <summary>Lease a popup WINDOW for an overlay subtree root (<c>PopupOptions.ConstrainToRootBounds = false</c>):
    /// the host creates the PAL popup window + its own swapchain and re-records the subtree into its own DrawList each
    /// frame (SceneRecorder root-override). Returns a token, or -1 when windowed popups are unavailable — callers fall
    /// back to constrained placement (WinUI's <c>DoesPlatformSupportWindowedPopup</c> gate).</summary>
    public Func<NodeHandle, PopupWindowMaterial, int>? OpenPopupWindow;
    /// <summary>Place a leased popup window: token + the logical menu CONTENT bounds in main-window DIP. The host
    /// inflates by the shadow insets, converts to physical px, and configures the hidden window's opening state.
    /// The host shows it only after its first subtree frame has been recorded and presented. <c>opensUp</c> = the menu
    /// opens upward (anchored at its bottom); <c>closedRatio</c> is the WinUI MenuPopupThemeTransition ratio
    /// (0.5 root menu, 0.67 cascaded submenu, 0 CommandBar) that drives the host-chrome open slide.</summary>
    public Action<int, RectF, bool, float>? SetPopupWindowBounds;
    /// <summary>Begin the desktop-acrylic close fade on a leased popup window's composition chrome (acrylic + shadow),
    /// synced with the engine's content fade. The window is disposed separately at finalize (<see cref="ClosePopupWindow"/>).</summary>
    public Action<int>? AnimatePopupClose;
    /// <summary>Release a leased popup window (hide + dispose the window and its swapchain) once the close fade has settled.</summary>
    public Action<int>? ClosePopupWindow;

    // ── Text-editing seams (host-wired in the AppHost ctor; consumed by EditableText) ───────────────────────────────
    // Wiring choice: the PAL seam INTERFACES are exposed directly (Hooks → Pal is a new interface-only edge; the
    // alternative — re-declaring delegate twins of IClipboard/ITextInputSink here — would duplicate a contract the
    // ownership map says Pal owns). Caret-blink and IME-caret-rect stay DELEGATE-shaped because the host owns knowledge
    // the control must not have: the CaretBlinker instance, and the window Scale for the DIP→physical-px conversion.

    /// <summary>The system clipboard (UI-thread only; <c>IPlatformApp.Clipboard</c>).</summary>
    public IClipboard? Clipboard;
    /// <summary>The focused window's IME/text-services seam (<c>IPlatformWindow.TextInput</c>): the focused editor
    /// registers its composition sink and flips <c>SetEditable</c> here. (Candidate-window placement goes through
    /// <see cref="ImeSetCaretRect"/> instead — it needs the host's DIP→px scale.)</summary>
    public IPlatformTextInput? TextInput;
    /// <summary>The host's text seam — the SAME layout pipeline the renderer measures with, so editor hit-test/caret
    /// queries agree with drawn glyph positions exactly.</summary>
    public IFontSystem? Fonts;

    /// <summary>Launch a URI in the OS default handler (<c>IPlatformApp.OpenUri</c>) — HyperlinkButton's WinUI
    /// NavigateUri step (Click first, then launch — HyperLinkButton_Partial.cpp:149-177). Host-wired in the AppHost
    /// ctor onto BOTH the host instance and the <see cref="Current"/> channel-default instance (static control
    /// factories have no component scope → no UseContext, so they reach the seam via the default). Null in a
    /// host-less tree → element construction/clicks never launch.</summary>
    public Action<string>? OpenUri;

    /// <summary>Raise a screen-reader announcement (a UIA "live region" notification) — <c>(text, assertive)</c>. The
    /// Windows backend wires this to <c>UiaRaiseNotificationEvent</c> on the window's UIA provider; assertive interrupts
    /// (errors), else polite (status / "Copied"). Null in a host-less / no-AT tree ⇒ a silent no-op.</summary>
    public Action<string, bool>? Announce;

    // ── OS file/folder drop seam (host → tree; the INBOUND twin of OpenUri) ──────────────────────────────────────────
    // Host-wired in the AppHost ctor onto BOTH this host instance and the Current.Default channel-default (so the
    // Windows backend's WM_DROPFILES handler — which has no component scope — reaches them through the default). The host
    // sets these to the InputDispatcher's ExternalDrag* methods and invokes them on the UI thread via the normal message
    // pump. Coordinates are window-DIP. Null in a host-less / drop-less tree ⇒ no OS drops. The Over/Leave delegates are
    // for backends that can supply hover feedback; the WM_DROPFILES backend uses Enter+Drop only.

    /// <summary>OS drag entered the window: window-DIP point + the dragged absolute paths + key modifiers. Returns the
    /// engine <see cref="DropEffect"/> the OS should reflect as the drag cursor (<see cref="DropEffect.None"/> ⇒ no-drop).</summary>
    public Func<Point2, string[], KeyModifiers, DropEffect>? ExternalDragEnter;
    /// <summary>OS drag moved within the window: window-DIP point + modifiers → the live effect (hover-capable backends only).</summary>
    public Func<Point2, KeyModifiers, DropEffect>? ExternalDragOver;
    /// <summary>OS drag left the window / was cancelled: end the external session (hover-capable backends only).</summary>
    public Action? ExternalDragLeave;
    /// <summary>OS drop committed inside the window: window-DIP point + modifiers. Returns true if a target accepted it.</summary>
    public Func<Point2, KeyModifiers, bool>? ExternalDrop;
    /// <summary>OS drop committed WITH the dragged paths (the hover-capable backend's IDropTarget::Drop reads the file
    /// list once, at drop, and passes it here — hover stayed data-free). Returns true if a target accepted it.</summary>
    public Func<Point2, string[], KeyModifiers, bool>? ExternalDropFiles;

    // ── Live drag state (host-wired; consumed by UseDragState / a DragPreviewLayer to render a cursor-following custom
    //    preview). DragEpoch is EDGE-triggered — see below; the pointer POSITION rides DragPosX/Y instead, so the epoch
    //    never bumps merely because the pointer moved. GetDragState reads the live session as a copied snapshot.
    //    Mirrors WindowChromeEpoch's pattern. ──
    /// <summary>Bumped by the host on the edges a preview's CONTENT depends on — session begin/end, the target under the
    /// pointer, the advisory effect, a refusal, the caption, and the settle window's start/expiry — NOT per frame (the
    /// chip follows through <see cref="DragPosX"/>/<see cref="DragPosY"/>). A component reads it to subscribe, then
    /// pulls <see cref="GetDragState"/> for the current snapshot on re-render.</summary>
    public Signal<int>? DragEpoch;
    /// <summary>Snapshot the live drag (active/kind/position/payload) — <see cref="DragState.Active"/> false when idle.</summary>
    public Func<DragState>? GetDragState;

    /// <summary>The live drag pointer position (window DIP) as two engine-owned scalar SIGNALS, written by the host on
    /// every drag move/tick. A drag preview BINDS its transform to these (compositor-only: no component re-render, no
    /// reconcile, no layout, 0 alloc per move) instead of re-rendering off <see cref="DragEpoch"/> — which is why the
    /// epoch is EDGE-triggered (begin/end, target/effect/caption change) rather than per-frame. Meaningless while no
    /// drag is live (they hold the last position).</summary>
    public FloatSignal? DragPosX;
    public FloatSignal? DragPosY;

    /// <summary>Arm the caret blinker for a (newly focused) editor's TEXT node; float = blink half-period ms
    /// (<c>IPlatformTextInput.CaretBlinkMs</c>).</summary>
    public Action<NodeHandle, float>? CaretFocus;
    /// <summary>Stop blinking for an editor's text node (focus lost).</summary>
    public Action<NodeHandle>? CaretBlur;
    /// <summary>An edit happened: snap the caret visible and restart the blink phase.</summary>
    public Action<NodeHandle>? CaretReset;
    /// <summary>Position the IME candidate window: the caret rect in window DIP — the HOST converts to physical px
    /// (it owns the window scale) before calling <c>IPlatformTextInput.SetCaretRectPx</c>.</summary>
    public Action<RectF>? ImeSetCaretRect;

    // ── SIP (touch keyboard) trigger seam (input-a11y.md §10; consumed by EditableText, host-wired in the AppHost ctor) ──
    /// <summary>True when the most recent focus-causing pointer was a TOUCH contact (host-wired to the dispatcher's
    /// <c>LastPointerKind</c>). EditableText gates the SIP show on this so the on-screen keyboard appears only on a touch
    /// focus, never a mouse/pen focus (the WinUI InputPaneHandler.cpp policy). Null in a host-less tree ⇒ treated as not
    /// touch (no SIP).</summary>
    public Func<bool>? LastPointerWasTouch;
    /// <summary>Request the OS touch keyboard for the focused editor (host-wired to
    /// <c>IPlatformWindow.TextInput.TryShowTouchKeyboard</c>). Called by EditableText on focus-gain when
    /// <see cref="LastPointerWasTouch"/> and the field is editable. Returns the platform's success (false on a desktop
    /// without a touch keyboard); null ⇒ no SIP wired (headless / SIP-less host).</summary>
    public Func<bool>? ShowTouchKeyboard;
    /// <summary>Dismiss the OS touch keyboard (host-wired to <c>IPlatformWindow.TextInput.TryHideTouchKeyboard</c>) —
    /// called by EditableText when focus leaves the editor for a non-editable target.</summary>
    public Func<bool>? HideTouchKeyboard;

    private readonly List<(object Owner, Action Action)> _afterAnimations = new();
    private readonly List<(object Owner, Action<NodeHandle> Action)> _subtreeDeactivated = new();

    /// <summary>True while a registered tree lifecycle needs frame ticks even when its compositor tracks are parked.
    /// Overlay close finalization uses this as a bounded watchdog wake; ordinary active tracks already wake the host.</summary>
    public Func<bool>? HasAfterAnimationWork;

    /// <summary>
    /// Host phase 7 hook: after <c>AnimEngine.Tick</c>, before record/present. Tree-level systems with retained
    /// animation lifecycles (overlays) use this to finalize settled visuals without a one-frame post-present delay.
    /// Registered by owner so multiple tree systems can coexist without clobbering each other.
    /// </summary>
    public void SetAfterAnimations(object owner, Action? action)
    {
        for (int i = 0; i < _afterAnimations.Count; i++)
        {
            if (!ReferenceEquals(_afterAnimations[i].Owner, owner)) continue;
            if (action is null) _afterAnimations.RemoveAt(i);
            else _afterAnimations[i] = (owner, action);
            return;
        }
        if (action is not null) _afterAnimations.Add((owner, action));
    }

    public void RunAfterAnimations()
    {
        for (int i = 0; i < _afterAnimations.Count; i++)
            _afterAnimations[i].Action();
    }

    /// <summary>
    /// Sibling of <see cref="SetAfterAnimations"/>: the host fires this synchronously when a retained subtree is
    /// deactivated (KeepAlive exit start, park, eviction). OverlayHost uses it to close entries whose anchor lives
    /// under that root — a dead-anchor tooltip must not wait for AfterAnimations prune.
    /// </summary>
    public void SetSubtreeDeactivatedListener(object owner, Action<NodeHandle>? action)
    {
        for (int i = 0; i < _subtreeDeactivated.Count; i++)
        {
            if (!ReferenceEquals(_subtreeDeactivated[i].Owner, owner)) continue;
            if (action is null) _subtreeDeactivated.RemoveAt(i);
            else _subtreeDeactivated[i] = (owner, action);
            return;
        }
        if (action is not null) _subtreeDeactivated.Add((owner, action));
    }

    public void RunSubtreeDeactivated(NodeHandle root)
    {
        for (int i = 0; i < _subtreeDeactivated.Count; i++)
            _subtreeDeactivated[i].Action(root);
    }

    public static readonly Context<InputHooks> Current = new(new InputHooks());
}

/// <summary>Provides a context value to its subtree (the React <c>Context.Provider</c>). One child.</summary>
public sealed record ContextProviderEl(object Channel, object? Value, Element Child) : Element
{
    public override ushort ElementTypeId => 4;
}

/// <summary>Fluent: <c>Ctx.Provide(MyContext, value, child)</c>.</summary>
public static class Ctx
{
    public static ContextProviderEl Provide<T>(Context<T> context, T value, Element child) => new(context, value, child);
}

/// <summary>
/// Reconcile-time shadow stack of provided context values. The reconciler pushes a provider's value before
/// reconciling its subtree (incl. nested component renders) and pops after; <c>UseContext</c> reads the top.
/// (Push/pop happen on the dirty reconcile path, never the zero-alloc paint half.)
/// </summary>
public static class ContextStack
{
    [ThreadStatic] private static List<(object ctx, object? val)>? _s;

    public static void Push(object ctx, object? val) => (_s ??= new()).Add((ctx, val));
    public static void Pop() { var s = _s!; s.RemoveAt(s.Count - 1); }

    public static bool TryGet(object ctx, out object? val)
    {
        var s = _s;
        if (s is not null)
            for (int i = s.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s[i].ctx, ctx)) { val = s[i].val; return true; }
        val = null;
        return false;
    }
}
