using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Headless;

public sealed class HeadlessPlatformApp : IPlatformApp
{
    private readonly List<HeadlessPopupWindow> _popupWindows = new();

    public IPlatformWindow CreateWindow(in WindowDesc desc) => new HeadlessWindow(desc);
    public IClipboard Clipboard { get; } = new HeadlessClipboard();

    /// <summary>The synthetic monitor work area returned by <see cref="GetWorkArea"/> (physical virtual-screen px).
    /// Tests set this to simulate a taskbar-clipped / multi-monitor desktop; default = a 1920×1080 primary monitor.</summary>
    public RectF WorkArea { get; set; } = new(0f, 0f, 1920f, 1080f);

    /// <summary>Optional per-point resolver for multi-monitor tests (wins over <see cref="WorkArea"/> when set) —
    /// e.g. return a different work-area rect for points on a synthetic secondary monitor.</summary>
    public Func<Point2, RectF>? WorkAreaResolver { get; set; }

    public RectF GetWorkArea(Point2 screenPointPx) => WorkAreaResolver?.Invoke(screenPointPx) ?? WorkArea;

    /// <summary>Every popup window created this run (never removed — disposal is observable via
    /// <see cref="HeadlessPopupWindow.Disposed"/>), for placement/lifecycle assertions.</summary>
    public IReadOnlyList<HeadlessPopupWindow> PopupWindows => _popupWindows;

    public IPlatformPopupWindow? CreatePopupWindow(in PopupWindowDesc desc)
    {
        var w = new HeadlessPopupWindow(desc);
        _popupWindows.Add(w);
        return w;
    }

    /// <summary>URIs passed to <see cref="OpenUri"/>, in call order — golden checks assert HyperlinkButton's WinUI
    /// Click→launch sequence (HyperLinkButton_Partial.cpp:149-177) without touching the OS.</summary>
    public List<string> OpenedUris { get; } = new();

    /// <summary>Headless analogue of Launcher::TryInvokeLauncher: record, never launch.</summary>
    public void OpenUri(string uri) => OpenedUris.Add(uri);

    public void Dispose() { }
}

/// <summary>Synthetic window: the test harness pushes input via <see cref="QueueInput"/>; the host drains it.</summary>
public sealed class HeadlessWindow : IPlatformWindow
{
    private readonly Queue<InputEvent> _queue = new();

    public HeadlessWindow(in WindowDesc desc)
    {
        ClientSizePx = desc.SizePx;
        _dpiScale = desc.Scale <= 0 ? 1f : desc.Scale;
        _zoom = ZoomLadder.Clamp(desc.Zoom);
        CustomFrame = desc.CustomFrame;
        Composited = desc.Composited;
        MinClientSizeDip = desc.MinClientSizeDip;
    }

    /// <summary>The <see cref="WindowDesc.CustomFrame"/> opt-in, recorded for assertions (no real NC concept headless).</summary>
    public bool CustomFrame { get; }
    /// <summary>The descriptor's opt-in minimum client size, retained for headless contract assertions.</summary>
    public Size2 MinClientSizeDip { get; }

    public NativeHandle Handle => new(0, NativeHandleKind.Headless);
    /// <summary>Settable (test seam): simulate a window resize / per-monitor DPI change (WM_DPICHANGED) mid-session —
    /// the host's EnsureSize watches BOTH px size and scale every frame, so the next RunFrame re-lays-out in the new
    /// DIP viewport (the multi-monitor DPI-hop regression).</summary>
    public Size2 ClientSizePx { get; set; }

    private float _dpiScale;   // the simulated OS DPI scale (the Scale SETTER's meaning — the WM_DPICHANGED seam)
    private float _zoom;       // the app zoom (SetZoom), folded into the effective getter below

    /// <summary>The DPI/zoom test seam, mirroring the Win32 split: the GETTER reports the EFFECTIVE scale (simulated
    /// OS DPI × app zoom — what the host's EnsureSize watches every frame); the SETTER keeps its documented meaning of
    /// the RAW OS DPI scale (simulate a per-monitor DPI change / WM_DPICHANGED mid-session — the DPI-hop seam the
    /// existing suites drive); app zoom is driven separately via <see cref="SetZoom"/>. Either road changes the getter
    /// and triggers the same full-relayout path.</summary>
    public float Scale { get => _dpiScale * _zoom; set => _dpiScale = value; }

    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.Zoom"/>
    public float Zoom => _zoom;

    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.SetZoom"/>
    public void SetZoom(float zoom) => _zoom = ZoomLadder.Clamp(zoom);
    public Action? PaintRequested { get; set; }   // unused headless (no modal resize loop)
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.ModalLoopTick"/>
    public Action? ModalLoopTick { get; set; }   // tests raise it by hand: headless has no modal loop of its own
    /// <summary>Settable synthetic OUTER rect in virtual-screen px (test seam for the pop-out coverage verdict). Default empty
    /// = "the backend cannot report it", exactly like the interface default.</summary>
    public RectF OuterBoundsPx { get; set; }
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.InModalLoop"/>
    public bool InModalLoop { get; set; }
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.SizedInModalLoop"/>
    public bool SizedInModalLoop { get; set; }
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.Composited"/>
    public bool Composited { get; set; }
    public CursorId LastCursor { get; private set; }
    public bool Shown { get; private set; }

    /// <summary>Settable synthetic screen position of client (0,0) — lets multi-monitor placement tests put the
    /// window anywhere on the virtual desktop. Default (0,0).</summary>
    public Point2 ClientOriginPx { get; set; }

    /// <summary>Last outer origin passed to <see cref="MoveToPx"/>, and how many times it was called — the headless
    /// twin of the Win32 <c>SetWindowPos(SWP_NOSIZE)</c> call, recorded rather than acted on. Deliberately does NOT
    /// touch <see cref="ClientSizePx"/>: a test asserting <c>MoveToPx</c> is a pure move (never a resize) reads
    /// <see cref="ClientSizePx"/> unchanged across the call.</summary>
    public Point2 LastMoveToPx { get; private set; }
    public int MoveToCount { get; private set; }
    public void MoveToPx(Point2 outerOriginPx) { LastMoveToPx = outerOriginPx; MoveToCount++; }

    public void QueueInput(in InputEvent e) => _queue.Enqueue(e);

    /// <summary>The synthetic off-screen park position <c>WM_POINTERLEAVE</c>'s hover-clear move carries (Win32
    /// <c>Win32Window.OffscreenDip</c>) — mirrored here so a headless gate can assert against the exact literal a
    /// captured node must never see as a real sample.</summary>
    public static readonly Point2 OffscreenDip = new(-10000f, -10000f);

    /// <summary>Headless twin of Win32's <c>WM_POINTERLEAVE</c>-while-held path (input-capture hardening): queues a
    /// <see cref="InputKind.PointerCancel"/> for <paramref name="pointerId"/> FIRST, then the off-screen
    /// <see cref="InputKind.PointerMove"/> park sample — the exact emission order <c>Win32Window</c>'s
    /// <c>WM_POINTERLEAVE</c> case now uses (cancel the held contact before the park move can reach a still-latched
    /// drag node). A gate drives this directly instead of going through a real OS message to exercise the dispatcher's
    /// reaction to that order without a Win32 window.</summary>
    public void QueuePointerLeaveWhileDown(uint pointerId, uint timestampMs, PointerKind kind = PointerKind.Mouse)
    {
        QueueInput(new InputEvent(InputKind.PointerCancel, default, 0, 0,
            Pointer: kind, TimestampMs: timestampMs, PointerId: pointerId));
        QueueInput(new InputEvent(InputKind.PointerMove, OffscreenDip, 0, 0,
            Pointer: kind, TimestampMs: timestampMs, PointerId: pointerId));
    }

    public int PumpInto(InputEventRing ring)
    {
        int n = 0;
        while (_queue.Count > 0) { ring.Write(_queue.Dequeue()); n++; }
        return n;
    }

    public void WaitForWork(int timeoutMs) { }

    // -- scripted frame-aligned scroll producer: a headless stand-in for DirectManipulation's touchpad contact stream.
    // QueueScrollDelta(dipX, dipY) accumulates until the next PumpScroll (one produced frame), which emits one
    // InputKind.Scroll Begin/Sample; QueueScrollLift emits the End. Exercises AppHost's PumpScroll -> dispatcher ->
    // ScrollHandle wiring headlessly.
    private float _pendingScrollDipX, _pendingScrollDipY;
    private bool _pendingScrollDeltaQueued;
    private bool _pendingScrollLift;
    private bool _scrollGestureOpen;
    private PointerKind _scrollPointerKind = PointerKind.Touchpad;
    private uint _scrollContactId = 1;   // fixed synthetic id: one concurrent scripted gesture
    private Point2 _scrollPointerDip;
    private Action<FluentGpu.Scroll.Runtime.ScrollInputEvent>? _scrollSink;

    /// <summary>Queue a frame-aligned scroll delta (DIP, positive = toward the content end), accumulated until the next
    /// <see cref="PumpScroll"/> call. Opens the gesture (a Begin) on the first pump after the gesture was closed.</summary>
    public void QueueScrollDelta(float dipX, float dipY, PointerKind kind = PointerKind.Touchpad, Point2 pointerDip = default)
    {
        _pendingScrollDipX += dipX;
        _pendingScrollDipY += dipY;
        _pendingScrollDeltaQueued = true;
        _scrollPointerKind = kind;
        if (pointerDip != default) _scrollPointerDip = pointerDip;
    }

    /// <summary>Queue the gesture's lift: an End on the NEXT <see cref="PumpScroll"/> call.</summary>
    public void QueueScrollLift() => _pendingScrollLift = true;

    /// <inheritdoc cref="IPlatformWindow.ScrollProducerLive"/>
    /// <remarks>Live while a gesture is open OR a delta/lift is queued for the next pump — DirectManipulation's producer is
    /// live from the contact's engage (before its first content update), so the frame that carries the first delta runs.</remarks>
    public bool ScrollProducerLive => _scrollGestureOpen || _pendingScrollDeltaQueued || _pendingScrollLift;

    /// <inheritdoc cref="IPlatformWindow.SetScrollInputSink"/>
    public void SetScrollInputSink(Action<FluentGpu.Scroll.Runtime.ScrollInputEvent>? sink) => _scrollSink = sink;

    /// <summary>Delivers a scroll input URGENTLY (synchronously through the host's sink, the way a real window's wheel
    /// message does) when a sink is installed, else queues it for the next pump. Gates use this for wheel notches.</summary>
    public void SendScroll(in FluentGpu.Scroll.Runtime.ScrollInputEvent e)
    {
        if (_scrollSink is { } sink) sink(e);
        else QueueInput(InputEvent.ForScroll(in e, PointerKind.Mouse));
    }

    /// <summary>A detented wheel notch at <paramref name="pointerDip"/> (positive notches = toward the content end).</summary>
    public void SendWheelNotch(Point2 pointerDip, float notches, long qpc = 0, KeyModifiers mods = KeyModifiers.None, bool horizontal = false)
        => SendScroll(new FluentGpu.Scroll.Runtime.ScrollInputEvent(FluentGpu.Scroll.Runtime.ScrollSource.MouseWheel,
            FluentGpu.Scroll.Runtime.ScrollGesture.Notch, qpc, pointerDip, horizontal ? notches : 0f, horizontal ? 0f : notches, 0, mods));

    /// <summary>The frame-aligned producer, modelled on the real one (DirectManipulation, <c>Win32DirectManipulation</c>):
    /// a TOUCHPAD stream is composition-timed — every event is stamped by the production rule,
    /// <see cref="FluentGpu.Scroll.Runtime.ContactStamp.ForFrame"/>: the NEXT tick's present (headless
    /// <c>PresentQpc = frame + refresh</c>, so the stamp is <c>frame + 2·refresh</c> — the same one-tick-on relation as
    /// on a real window), the first render turn guaranteed to see it — flagged <c>PresentTimed</c>, and carries
    /// <c>ArrivalQpc</c> = the clock's <see cref="FrameClock.NowQpc"/> (when it was observed); a frame with no queued
    /// delta emits nothing (DirectManipulation raises no content update without movement). A TOUCH pan is device-timed
    /// at the frame instant.</summary>
    public int PumpScroll(in FrameClock clock, InputEventRing ring)
    {
        int n = 0;
        bool touch = _scrollPointerKind == PointerKind.Touch;
        var src = touch ? FluentGpu.Scroll.Runtime.ScrollSource.Touch : FluentGpu.Scroll.Runtime.ScrollSource.Touchpad;
        long stamp = touch ? clock.FrameQpc : FluentGpu.Scroll.Runtime.ContactStamp.ForFrame(in clock, clock.NowQpc);
        long arrival = touch ? 0L : clock.NowQpc;   // a device-timed stamp already is the arrival (0 ⇒ same as Qpc)
        bool presentTimed = !touch;
        if (_pendingScrollDeltaQueued)
        {
            if (!_scrollGestureOpen)
            {
                ring.Write(InputEvent.ForScroll(new FluentGpu.Scroll.Runtime.ScrollInputEvent(src, FluentGpu.Scroll.Runtime.ScrollGesture.Begin,
                    stamp, _scrollPointerDip, 0f, 0f, _scrollContactId, KeyModifiers.None) { PresentTimed = presentTimed, ArrivalQpc = arrival }, _scrollPointerKind));
                _scrollGestureOpen = true;
                n++;
            }
            ring.Write(InputEvent.ForScroll(new FluentGpu.Scroll.Runtime.ScrollInputEvent(src, FluentGpu.Scroll.Runtime.ScrollGesture.Sample,
                stamp, _scrollPointerDip, _pendingScrollDipX, _pendingScrollDipY, _scrollContactId, KeyModifiers.None) { PresentTimed = presentTimed, ArrivalQpc = arrival }, _scrollPointerKind));
            n++;
            _pendingScrollDipX = 0f; _pendingScrollDipY = 0f; _pendingScrollDeltaQueued = false;
        }
        if (_pendingScrollLift && _scrollGestureOpen)
        {
            ring.Write(InputEvent.ForScroll(new FluentGpu.Scroll.Runtime.ScrollInputEvent(src, FluentGpu.Scroll.Runtime.ScrollGesture.End,
                stamp, _scrollPointerDip, 0f, 0f, _scrollContactId, KeyModifiers.None) { PresentTimed = presentTimed, ArrivalQpc = arrival }, _scrollPointerKind));
            _scrollGestureOpen = false;
            _pendingScrollLift = false;
            n++;
        }
        return n;
    }

    public void SetCursor(CursorId id) => LastCursor = id;
    public void SetTitle(StringId title) { }
    public void Show() { Shown = true; IsVisible = true; ShowCalls++; }

    /// <summary>How many times <see cref="Show"/> was called (test seam): the pop-out reveal must show its window exactly once.</summary>
    public int ShowCalls { get; private set; }

    /// <summary>The last <see cref="SetTopmost"/> value (test seam; false until set).</summary>
    public bool Topmost { get; private set; }

    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.SetTopmost"/>
    public void SetTopmost(bool topmost) => Topmost = topmost;

    /// <summary>Settable visibility (test seam), mirroring Win32's live <c>WS_VISIBLE</c> read: <see cref="Hide"/> clears
    /// it, <see cref="Show"/> sets it. Defaults to TRUE so a headless host that never calls <see cref="Show"/> is not
    /// parked — headless has no real screen, and every existing gate drives an unshown window.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Settable (test seam): the OS compositor cloaks the window (another virtual desktop). False by default.</summary>
    public bool IsCloaked { get; set; }

    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.Hide"/>
    public void Hide() => IsVisible = false;

    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.CloseRequested"/>
    public Func<CloseReason, bool>? CloseRequested { get; set; }
    public IPlatformTextInput TextInput { get; } = new HeadlessTextInput();

    // ── custom-titlebar mirror: recorded call-lists + settable state (golden checks assert against these) ────────────

    /// <summary>Settable placement (test seam). <see cref="ToggleMaximize"/> flips it and emits
    /// <see cref="InputKind.WindowStateChanged"/> exactly like the Win32 backend's WM_SIZE transition.</summary>
    public WindowState State { get; set; } = WindowState.Normal;

    private bool _active = true;
    /// <summary>Settable activation (test seam): flipping emits WindowFocus/WindowBlur like Win32 WM_ACTIVATE.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            QueueInput(new InputEvent(value ? InputKind.WindowFocus : InputKind.WindowBlur, default, 0, 0));
        }
    }

    public int MinimizeCount { get; private set; }
    public int ToggleMaximizeCount { get; private set; }
    public int SetFullscreenCount { get; private set; }
    public bool IsFullscreen { get; private set; }
    public int CloseCount { get; private set; }
    /// <summary>Settable (test seam): the window was closed by the user / OS — a detached child's host is reaped by the parent
    /// loop on seeing this. False by default (headless windows are never closed by the OS).</summary>
    public bool IsClosed { get; set; }

    public void Minimize() { MinimizeCount++; State = WindowState.Minimized; }

    public void ToggleMaximize()
    {
        ToggleMaximizeCount++;
        State = State == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        QueueInput(new InputEvent(InputKind.WindowStateChanged, default, 0, 0));
    }

    public void SetFullscreen(bool fullscreen) { SetFullscreenCount++; IsFullscreen = fullscreen; }

    public void CloseWindow() => CloseCount++;

    /// <summary>The most recent region push (copied), for drag-band/island/button-rect assertions.</summary>
    public TitleBarRegion[] LastTitleBarRegions { get; private set; } = [];
    public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) => LastTitleBarRegions = regions.ToArray();

    public void Dispose() { }
}
