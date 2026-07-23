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
}

/// <summary>
/// A per-frame tick the host bumps (only while something subscribes, so it's free when idle). A tree-level concern that
/// must poll each frame — e.g. an overlay driving a timed close animation to completion — reads
/// <c>UseContext(FrameClock.Tick)</c> to re-render every frame for as long as it's mounted.
/// </summary>
public static class FrameClock
{
    public static readonly Context<long> Tick = new(0L);
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
public readonly record struct DetachedWindowRequest(
    string Title, FluentGpu.Foundation.Size2 InitialSizeDip, Component Content, bool AlwaysOnTop = true);

/// <summary>A live handle to a detached video window (see <see cref="InputHooks.OpenDetachedWindow"/>).</summary>
public interface IDetachedVideoWindow
{
    /// <summary>True until the window is closed/reaped.</summary>
    bool IsOpen { get; }
    /// <summary>Toggle persistent always-on-top.</summary>
    void SetTopmost(bool topmost);
    /// <summary>Move/resize the window (outer rect, physical virtual-screen px).</summary>
    void SetBounds(FluentGpu.Foundation.RectF outerBoundsPx);
    /// <summary>Close the window (the pop-out docks back to inline).</summary>
    void Close();
}

public sealed class InputHooks
{
    /// <summary>Return true to consume the key. Set by an open overlay; cleared when it closes.</summary>
    public Func<int, bool>? KeyPreview;
    public bool Preview(int key) => KeyPreview?.Invoke(key) ?? false;

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
    /// <summary>Open a movable/resizable, always-on-top DETACHED video window hosting the request's content in its OWN
    /// window + scene + swapchain (the pop-out mini-player). Returns a handle, or null when unavailable (headless, the
    /// async render path, or a backend without secondary swapchains). Host-wired to <c>AppHost.OpenDetachedWindow</c>.</summary>
    public Func<DetachedWindowRequest, IDetachedVideoWindow?>? OpenDetachedWindow;
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
    /// inflates by the shadow insets, converts to physical px, shows the window (without activating), and — for a
    /// desktop-acrylic popup — configures + plays the open motion. <c>opensUp</c> = the menu opens upward (anchored at
    /// its bottom); <c>closedRatio</c> is the WinUI MenuPopupThemeTransition ratio (0.5 root menu, 0.67 cascaded submenu)
    /// that drives the open slide distance + the plate ScaleY.</summary>
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
    //    preview). DragEpoch bumps each frame while a typed drag (in-app DragSource OR an OS file drag) is live, plus once
    //    when it ends; GetDragState reads the live session as a copied snapshot. Mirrors WindowChromeEpoch's pattern. ──
    /// <summary>Bumped by the host while a drag is live (and once on end): a component reads it to subscribe, then pulls
    /// <see cref="GetDragState"/> for the current snapshot on re-render.</summary>
    public Signal<int>? DragEpoch;
    /// <summary>Snapshot the live drag (active/kind/position/payload) — <see cref="DragState.Active"/> false when idle.</summary>
    public Func<DragState>? GetDragState;

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
