using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Pal;

/// <summary>OS user-preference parameters the engine honors (WinUI reads them through SystemParametersInfo /
/// the registry). The platform writes them ONCE at startup (Win32: HKCU + SPI reads); headless keeps the
/// defaults for determinism. Read anywhere (controls included) — plain statics, no per-frame cost.</summary>
public static class SystemParams
{
    /// <summary>HKCU "Control Panel\Desktop" MenuShowDelay (ms) — the cascading-menu hover open/close delay.
    /// WinUI: CascadingMenuHelper.cpp:83-95 with DefaultMenuShowDelay = 400 fallback (MenuFlyout_Partial.h:13).</summary>
    public static float MenuShowDelayMs { get; set; } = 400f;

    /// <summary>SPI_GETMENUDROPALIGNMENT: true = menus drop RIGHT-aligned (left-handed convention) — WinUI uses it
    /// to pick the slider tooltip / menu side (Slider_Partial.cpp:2094-2099).</summary>
    public static bool MenuDropRightAligned { get; set; }

    /// <summary>Ticks-per-second of the platform's high-resolution input clock (<see cref="InputEvent.QpcTicks"/>).
    /// Win32 sets it once at platform init (QPC and <c>Stopwatch</c> share the domain, so this is
    /// <c>Stopwatch.Frequency</c>); headless leaves 0 = "no high-res clock" — the velocity estimator then falls back
    /// to millisecond <see cref="InputEvent.TimestampMs"/> arithmetic, keeping the gates deterministic.</summary>
    public static long QpcFrequency { get; set; }

    /// <summary>SPI_GETWHEELSCROLLLINES — the user's "roll the mouse wheel to scroll N lines" preference (Control Panel ▸
    /// Mouse ▸ Wheel). Detented-wheel distance scales by <c>lines/3</c> (scroll-feel-rework-v2 §3.2; 3 = the OS default,
    /// so the multiplier is 1.0 out of the box). The Win32 producer reads it once at startup and re-reads it on
    /// WM_SETTINGCHANGE; headless keeps the default (deterministic gates). <see cref="WheelScrollPage"/> = the "one screen
    /// at a time" setting (SPI returns WHEEL_PAGESCROLL), where a notch pages instead of scaling by lines.</summary>
    public static int WheelScrollLines { get; set; } = 3;

    /// <summary>SPI_GETWHEELSCROLLCHARS — the horizontal-wheel analogue of <see cref="WheelScrollLines"/> (characters per
    /// notch); default 3 so the multiplier is 1.0.</summary>
    public static int WheelScrollChars { get; set; } = 3;

    /// <summary>True when the user chose "scroll one screen at a time" (SPI_GETWHEELSCROLLLINES == WHEEL_PAGESCROLL): a
    /// detented notch pages (~0.875·viewport) rather than scaling by <see cref="WheelScrollLines"/>.</summary>
    public static bool WheelScrollPage { get; set; }
}

public enum InputKind : byte
{
    PointerMove = 1, PointerDown = 2, PointerUp = 3, Key = 4, Wheel = 5, Char = 6,
    KeyUp = 7,
    /// <summary>The platform cancelled an in-flight pointer interaction (capture lost, touch cancel).</summary>
    PointerCancel = 8,
    /// <summary>The window lost activation (WM_ACTIVATE WA_INACTIVE) — light-dismiss overlays close, pressed state clears.</summary>
    WindowBlur = 9,
    WindowFocus = 10,
    /// <summary>The window's placement changed (normal ↔ maximized/minimized) — a custom titlebar re-glyphs max↔restore.</summary>
    WindowStateChanged = 11,

    // ── the phase-tagged scroll contract (docs/plans/scroll-feel-rework-design.md §1) ──────────────────────────────
    // Six additive kinds produced by the phase producers (DirectManipulation PTP, the touch arena, the hardened
    // wheel-fallback classifier, macOS NSEvent later; scripted by the headless producer for gates). Contact deltas are
    // applied 1:1 by the integrator; Momentum* deltas are OS-owned inertia applied verbatim. The legacy Wheel kind
    // stays for detented mouse notches + element-level OnPointerWheel. Fields: ScrollDelta/ScrollDeltaX carry the DIP
    // deltas (same sign convention), DeviceClassRaw the producer tag, QpcTicks the per-packet high-res stamp.
    /// <summary>Contact engaged (touch pan claimed / DManip RUNNING entered). Delta may be 0.</summary>
    ScrollBegin = 12,
    /// <summary>Contact delta — 1:1 raw application. Ring-coalesces per frame (deltas sum, newest stamp).</summary>
    ScrollUpdate = 13,
    /// <summary>Contact lifted with NO OS momentum to follow (hard lift). Never coalesces.</summary>
    ScrollEnd = 14,
    /// <summary>OS inertia starts (DManip INERTIA). Never coalesces.</summary>
    MomentumBegin = 15,
    /// <summary>OS inertia delta — applied verbatim (no second integrator). Ring-coalesces per frame.</summary>
    MomentumUpdate = 16,
    /// <summary>OS inertia finished or cancelled. Never coalesces.</summary>
    MomentumEnd = 17,
}

/// <summary>Producer tag on scroll-phase events (<see cref="InputEvent.DeviceClassRaw"/>) — the integrator picks its
/// momentum-ownership row from this (design §1): 1 = precision touchpad via DManip, 2 = touch (engine owns momentum),
/// 3 = detented mouse wheel, 4 = hi-res wheel fallback (no DManip), 5 = reserved for macOS NSTrackpad.</summary>
public enum ScrollDeviceClass : byte { Unset = 0, Touchpad = 1, Touch = 2, WheelDetented = 3, WheelHiResFallback = 4, NSTrackpad = 5 }

/// <summary>Window placement, surfaced for custom-titlebar chrome (<see cref="IPlatformWindow.State"/>).</summary>
public enum WindowState : byte { Normal = 0, Maximized = 1, Minimized = 2 }

/// <summary>Non-client classification for one engine-reported titlebar rect (engine → WM_NCHITTEST). <c>Client</c>
/// marks an INTERACTIVE ISLAND (search box, back/pane buttons) the engine keeps; <c>Caption</c> is the OS drag-move
/// band; the three buttons get HTMIN/HTMAX/HTCLOSE so Win11 shows the snap-layouts flyout over Max.</summary>
public enum TitleBarHit : byte { Client = 0, Caption = 1, MinButton = 2, MaxButton = 3, CloseButton = 4 }

/// <summary>One reported titlebar region: a rect in CLIENT DIP (the engine's space) + its non-client classification.
/// Pushed on titlebar relayout only (push-on-change — never per frame). First match wins at hit-test, so callers list
/// interactive islands and buttons BEFORE the catch-all <see cref="TitleBarHit.Caption"/> band.</summary>
public readonly record struct TitleBarRegion(RectF RectDip, TitleBarHit Hit);

/// <summary>
/// POD input event drained from the host-owned ring once per frame (no C# events across the seam).
/// <paramref name="ScrollDelta"/> (Wheel only) is the VERTICAL wheel in DIP for ELEMENT-level handlers (PointerWheel),
/// oriented so positive = scroll toward the content end (offset increases); <paramref name="ScrollDeltaX"/> is the
/// HORIZONTAL wheel (WM_POINTERHWHEEL / trackpad two-finger horizontal), same DIP + sign convention on the X axis.
/// <paramref name="WheelNotch"/>/<paramref name="WheelNotchX"/> carry the signed device notch count already scaled by the
/// user's SPI wheel-lines preference (<c>notches·<see cref="SystemParams.WheelScrollLines"/>/3</c>; page mode ⇒ a per-notch
/// page multiplier — scroll-feel-rework-v2 §3.2), viewport-independent. A <see cref="PointerKind.Mouse"/> viewport wheel scrolls max(48 DIP, 10%·viewport) per notch
/// (our chosen distance — WinUI's actual wheel distance is InteractionTracker-internal). A <see cref="PointerKind.Touchpad"/>
/// uses the calibrated DIP deltas directly, tracks content synchronously, and measures the packet stream for its kinetic
/// tail. A synthetic event that sets only <paramref name="ScrollDelta"/> scrolls that DIP directly.
/// <paramref name="QpcTicks"/> is the per-packet high-resolution stamp (POINTER_INFO.PerformanceCount; ticks of
/// <see cref="SystemParams.QpcFrequency"/>; 0 = unavailable → millisecond fallback) feeding the release-velocity
/// estimator; <paramref name="ScrollPhaseSeq"/> a per-gesture packet ordinal (diagnostics); <paramref name="DeviceClassRaw"/>
/// a <see cref="ScrollDeviceClass"/> producer tag on scroll-phase events.
/// <paramref name="Button"/>: 0 = left, 1 = right, 2 = middle. <paramref name="Mods"/> is the modifier chord at the
/// time of the event (pump-captured); <paramref name="IsRepeat"/> = keyboard auto-repeat (lParam bit 30);
/// <paramref name="TimestampMs"/> = the platform message time (drives double/triple-click detection in the dispatcher).
/// <paramref name="PointerId"/> identifies the contact (mouse = 0; touch/pen carry the OS pointer id) so the ring
/// coalesces moves and the dispatcher captures per contact; <paramref name="Pressure"/> is the normalized contact
/// pressure (mouse = 1; touch/pen report 0..1). WinUI: PointerInputProcessor.cpp / GetPointerInfo POINTER_INFO.
/// </summary>
public readonly record struct InputEvent(
    InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0f,
    KeyModifiers Mods = KeyModifiers.None, PointerKind Pointer = PointerKind.Mouse,
    bool IsRepeat = false, uint TimestampMs = 0, uint PointerId = 0, float Pressure = 1f,
    float ScrollDeltaX = 0f,    // trailing-optional (mouse call sites unchanged); the HORIZONTAL wheel delta (DIP)
    float WheelNotch = 0f,      // VERTICAL wheel device notch count (signed; rawAmount/120) — viewport-independent
    float WheelNotchX = 0f,     // HORIZONTAL wheel device notch count (signed); the dispatcher scales notch → DIP
    long QpcTicks = 0,          // per-packet high-res stamp (SystemParams.QpcFrequency ticks; 0 = ms fallback)
    byte ScrollPhaseSeq = 0,    // per-gesture packet ordinal on scroll-phase events (wraps; diagnostics only)
    byte DeviceClassRaw = 0);   // ScrollDeviceClass producer tag on scroll-phase events (0 = not a scroll-phase event)

/// <summary>One pre-coalesce velocity sample (design §2): the per-frame event coalescing keeps only the newest
/// move/summed delta, which would cap release-velocity fidelity at frame resolution — so every coalesced-away
/// touch/pen move (and, later, every raw scroll-phase packet) deposits its <c>(position, stamp)</c> here instead.
/// The dispatcher drains this alongside the events and feeds the IMPULSE estimator; feeding is idempotent (the
/// estimator rejects non-monotonic stamps), so no consumption bookkeeping is needed. X/Y are the absolute DIP
/// position for pointer moves (scroll-phase packets, Phase 2, carry deltas).
/// <para><paramref name="Seq"/> is the depositing scroll-phase packet's <see cref="InputEvent.ScrollPhaseSeq"/> ordinal
/// (0 for touch/pen move deposits, which have no scroll-phase sequence). Tagging the sample with BOTH the pointer id and
/// the monotonic phase sequence (scroll-feel-rework-v2 §3.4) lets a consumer that latches one gesture drain only that
/// gesture's samples — an interleaved event that splits a frame into two <see cref="InputKind.ScrollUpdate"/>s can never
/// replay a later packet's deposit against an earlier base. The estimator's strictly-increasing-stamp rejection already
/// covers the single-latched-gesture case; the seq tag hardens the cross-gesture case (and the DirectManipulation sink).</para></summary>
public readonly record struct PointerVelSample(uint PointerId, float X, float Y, uint TimestampMs, long QpcTicks, byte Seq = 0);

/// <summary>
/// Drained by the host each frame (drain-to-empty, single contiguous span — <c>AppHost.RunFrame</c> Clears, the window
/// writes, then the dispatcher consumes the whole <see cref="Drain"/> span). Fixed-capacity slab: never allocates after
/// construction. A <see cref="InputKind.PointerMove"/> whose previous unconsumed move for the SAME <see cref="InputEvent.PointerId"/>
/// is still in the slab overwrites it in place (the dispatcher only needs the latest position per contact between frames —
/// WinUI's <c>GetPointerFrameInfoHistory</c> OS-side coalescing); Down/Up/Key/Char/Cancel never coalesce; consecutive
/// <see cref="InputKind.Wheel"/> events at the same position accumulate <see cref="InputEvent.ScrollDelta"/> (matching the
/// dispatcher's per-event accumulation into the scroll target). On slab overflow of a non-coalescible event the OLDEST
/// pending move is dropped (or, if none, the incoming event is dropped) — bounded, zero-growth.
/// </summary>
public sealed class InputEventRing
{
    private const int Capacity = 512;
    /// <summary>Distinct concurrent <see cref="InputEvent.PointerId"/>s tracked between drains: mouse (0) + the 10-contact
    /// capture cap + the reserved NC-synthesis id, with headroom. An id past this many is simply not coalesced (correct,
    /// just an extra slot used) — never grows.</summary>
    private const int IdSlots = 16;

    private readonly InputEvent[] _buf = new InputEvent[Capacity];
    private int _count;

    // Per-id last-pending-move bookkeeping: a fixed open-addressed table mapping an arbitrary uint id → a small slot,
    // each slot remembering the index of that id's latest move in _buf (-1 = none). Reset on every Drain/Clear, so it is
    // allocation-free at steady state.
    private readonly uint[] _idKey = new uint[IdSlots];      // the id occupying this slot
    private readonly bool[] _idUsed = new bool[IdSlots];     // slot occupied this frame
    private readonly int[] _lastMove = new int[IdSlots];     // index in _buf of that id's pending move (-1 = none)

    public void Write(in InputEvent e)
    {
        if (e.Kind == InputKind.PointerMove)
        {
            int slot = IdSlot(e.PointerId);
            if (slot >= 0 && _lastMove[slot] >= 0)
            {
                // Coalesce: overwrite this id's pending move in place — but a touch/pen sample the estimator needs
                // must not vanish with it: deposit the OVERWRITTEN move into the velocity side ring first (design §2).
                ref InputEvent prev = ref _buf[_lastMove[slot]];
                if (prev.Pointer is PointerKind.Touch or PointerKind.Pen)
                    PushVelocitySample(new PointerVelSample(prev.PointerId, prev.PositionPx.X, prev.PositionPx.Y, prev.TimestampMs, prev.QpcTicks));
                prev = e;
                return;
            }
            int idx = Append(in e);
            if (slot >= 0) _lastMove[slot] = idx;
            return;
        }

        if (e.Kind == InputKind.Wheel && _count > 0)
        {
            ref InputEvent prev = ref _buf[_count - 1];
            // Coalesce by SCROLLER TARGET, not by exact PositionPx equality (scroll-feel-rework-v2 §3.4): same device
            // (Pointer) + same PointerId ⇒ same scroller this frame. The old exact-position key split a frame's notches
            // into separate events whenever a resting mouse's WM_POINTERWHEEL packets carried sub-pixel-jittered coords —
            // each re-running the dispatcher's CancelGesture + two hit-tests. The ring can't hit-test, so the pointer id is
            // its target proxy; the newest position survives (the dispatcher re-hit-tests the summed event once).
            if (prev.Kind == InputKind.Wheel && prev.Pointer == e.Pointer && prev.PointerId == e.PointerId)
            {
                if (ScrollTrace.On)
                    ScrollTrace.Coalesce((byte)InputKind.Wheel, e.ScrollDelta, e.ScrollDeltaX,
                        prev.ScrollDelta + e.ScrollDelta, prev.ScrollDeltaX + e.ScrollDeltaX, e.QpcTicks);
                prev = prev with { ScrollDelta = prev.ScrollDelta + e.ScrollDelta, ScrollDeltaX = prev.ScrollDeltaX + e.ScrollDeltaX,
                                   WheelNotch = prev.WheelNotch + e.WheelNotch, WheelNotchX = prev.WheelNotchX + e.WheelNotchX,
                                   PositionPx = e.PositionPx, TimestampMs = e.TimestampMs, PointerId = e.PointerId, QpcTicks = e.QpcTicks };
                return;
            }
        }

        // Scroll-phase Update pair: deltas sum per frame (the vsync resample), newest stamp/seq survives; the raw
        // packet's timing goes to the velocity side ring. Begin/End/Momentum-transition kinds NEVER coalesce (§1).
        if (e.Kind is InputKind.ScrollUpdate or InputKind.MomentumUpdate && _count > 0)
        {
            ref InputEvent prev = ref _buf[_count - 1];
            if (prev.Kind == e.Kind && prev.PointerId == e.PointerId)
            {
                // Contact packets feed release velocity; momentum packets don't. Axis order: PointerVelSample.X is the
                // HORIZONTAL channel (ScrollDeltaX) and .Y the VERTICAL (ScrollDelta) — the same X/Y semantics the
                // touch-move deposit above uses. (These were SWAPPED here once: the estimator then saw the pan axis as
                // flat plateaus + per-frame spikes and inflated release velocity ~4-6× whenever ≥2 packets folded into
                // one frame — the oversized-fling / violent-edge-bounce defect. gate.scroll.phase-release-velocity
                // pins the corrected order.)
                if (e.Kind == InputKind.ScrollUpdate)
                    PushVelocitySample(new PointerVelSample(prev.PointerId, prev.ScrollDeltaX, prev.ScrollDelta, prev.TimestampMs, prev.QpcTicks, prev.ScrollPhaseSeq));
                if (ScrollTrace.On)
                    ScrollTrace.Coalesce((byte)e.Kind, e.ScrollDelta, e.ScrollDeltaX,
                        prev.ScrollDelta + e.ScrollDelta, prev.ScrollDeltaX + e.ScrollDeltaX, e.QpcTicks);
                prev = prev with { ScrollDelta = prev.ScrollDelta + e.ScrollDelta, ScrollDeltaX = prev.ScrollDeltaX + e.ScrollDeltaX,
                                   PositionPx = e.PositionPx, TimestampMs = e.TimestampMs, QpcTicks = e.QpcTicks, ScrollPhaseSeq = e.ScrollPhaseSeq };
                return;
            }
        }

        Append(in e);   // Down/Up/Key/Char/Cancel/window/phase-transition events: never coalesce
    }

    // ── the velocity side ring (design §2) ────────────────────────────────────────────────────────────────────────
    private const int VelCapacity = 64;   // ≥4× headroom over a 1 kHz device at 60 Hz frames (≈16 samples/frame)
    private readonly PointerVelSample[] _vel = new PointerVelSample[VelCapacity];
    private int _velCount;

    /// <summary>Producers (and the ring's own coalescing) deposit pre-coalesce samples here. Overflow drops the
    /// OLDEST (velocity is a trailing estimate — the newest samples carry it); one shift on a rare path, zero growth.</summary>
    public void PushVelocitySample(in PointerVelSample s)
    {
        if (ScrollTrace.On) ScrollTrace.VelDeposit(s.X, s.Y, s.TimestampMs, s.QpcTicks);
        if (_velCount == VelCapacity)
        {
            Array.Copy(_vel, 1, _vel, 0, VelCapacity - 1);
            _velCount = VelCapacity - 1;
        }
        _vel[_velCount++] = s;
    }

    /// <summary>The frame's pre-coalesce velocity samples, chronological. Drained alongside <see cref="Drain"/>.</summary>
    public ReadOnlySpan<PointerVelSample> DrainVelocitySamples() => _vel.AsSpan(0, _velCount);

    public ReadOnlySpan<InputEvent> Drain() => _buf.AsSpan(0, _count);

    public void Clear()
    {
        _count = 0;
        _velCount = 0;
        for (int i = 0; i < IdSlots; i++) { _idUsed[i] = false; _lastMove[i] = -1; }
    }

    private int Append(in InputEvent e)
    {
        if (_count == Capacity && !TryEvictOldestMove())
            return -1;   // slab full of non-coalescible events: drop the incoming one (bounded, never grows)
        int idx = _count++;
        _buf[idx] = e;
        return idx;
    }

    /// <summary>Overflow relief: drop the OLDEST pending <see cref="InputKind.PointerMove"/>, compacting the slab so the
    /// freed slot is at the tail. Returns false when no move can be dropped (caller drops the incoming event instead).</summary>
    private bool TryEvictOldestMove()
    {
        int victim = -1;
        for (int i = 0; i < _count; i++)
            if (_buf[i].Kind == InputKind.PointerMove) { victim = i; break; }
        if (victim < 0) return false;

        for (int i = victim; i < _count - 1; i++) _buf[i] = _buf[i + 1];
        _count--;

        // Indices shifted left by one for everything after the victim — rebuild the per-id pending-move map.
        for (int s = 0; s < IdSlots; s++)
        {
            if (!_idUsed[s]) continue;
            int m = _lastMove[s];
            if (m == victim) _lastMove[s] = -1;
            else if (m > victim) _lastMove[s] = m - 1;
        }
        return true;
    }

    /// <summary>Map an arbitrary pointer id to a fixed slot (open-addressed, linear probe). Returns -1 when the table is
    /// full this frame — that id's moves then simply do not coalesce (still correct), keeping the path allocation-free.</summary>
    private int IdSlot(uint id)
    {
        int start = (int)(id % IdSlots);
        for (int p = 0; p < IdSlots; p++)
        {
            int s = start + p;
            if (s >= IdSlots) s -= IdSlots;
            if (!_idUsed[s]) { _idUsed[s] = true; _idKey[s] = id; _lastMove[s] = -1; return s; }
            if (_idKey[s] == id) return s;
        }
        return -1;
    }
}

public interface IPlatformApp : IDisposable
{
    IPlatformWindow CreateWindow(in WindowDesc desc);

    /// <summary>The system clipboard (UI-thread only).</summary>
    IClipboard Clipboard { get; }

    /// <summary>Launch <paramref name="uri"/> in the OS default handler (browser/mail) — the WinUI
    /// <c>Launcher::TryInvokeLauncher</c> step of HyperlinkButton.OnClick (Click raised first at :166, then the
    /// launch at :172 — microsoft-ui-xaml dxaml\xcp\dxaml\lib\HyperLinkButton_Partial.cpp:149-177). Fire-and-forget
    /// on the UI thread; failures are swallowed (WinUI's TryInvokeLauncher is equally best-effort). Headless
    /// implementations record the URI instead of launching.</summary>
    void OpenUri(string uri);

    /// <summary>
    /// Raised when a SECOND launch of a single-instance app is redirected to this (already-running) instance, carrying the
    /// new launch's activation payload — the deep-link URI (<c>wavee://callback?…</c>) or the empty string for a bare
    /// focus-only relaunch. The inbound producer (<c>FluentGpu.WindowsApi.Activation.SingleInstanceGate</c>) forwards it
    /// from the exiting second instance via <c>WM_COPYDATA</c>; the Win32 PAL reconstructs the string inside its
    /// <c>WndProc</c> and invokes this. Mirrors the outbound <see cref="OpenUri"/> seam shape (this is its inbound twin).
    /// <para>
    /// THREADING CONTRACT — delivered on the UI thread. The Win32 backend raises it synchronously from
    /// <c>WM_COPYDATA</c>, which the OS dispatches on the window's own (UI) thread, so subscribers may touch
    /// non-thread-safe host state (e.g. <c>AppHost.WakeFrame</c>) directly. A cross-thread producer (a notification COM
    /// activator firing on a threadpool/agile-COM thread) MUST <c>PostMessage</c> to hop onto the UI thread before
    /// raising it — never invoke it off-thread. The default implementation never fires (headless / non-redirecting
    /// backends), keeping it test-neutral; it is a default-interface-method event so backends opt in without every
    /// <see cref="IPlatformApp"/> implementer having to declare it.
    /// </para>
    /// </summary>
    event Action<string>? ActivationRedirected { add { } remove { } }

    /// <summary>
    /// Raised when the OS color settings change — the user flips Windows' app dark/light mode or changes the system
    /// accent (Settings ▸ Colors). Carries no payload: subscribers re-read the current OS state (the host facade exposes
    /// it) and decide what to apply, so a single signal covers both the theme and the accent. The Win32 backend raises it
    /// from <c>WM_SETTINGCHANGE</c> with the <c>"ImmersiveColorSet"</c> area, dispatched on the window's own (UI) thread,
    /// so subscribers may touch non-thread-safe host state (e.g. <c>AppHost.WakeFrame</c>) directly. A default-interface
    /// no-op so headless / non-Windows backends opt out for free.
    /// </summary>
    event Action? SystemColorsChanged { add { } remove { } }

    /// <summary>
    /// The WORK AREA (desktop minus taskbar/docked bars) of the monitor containing <paramref name="screenPointPx"/>,
    /// in physical virtual-screen px — the multi-monitor placement seam WinUI's windowed popups use
    /// (Popup.cpp monitor-bounds placement; <c>DXamlCore::CalculateAvailableMonitorRect</c>,
    /// FlyoutBase_Partial.cpp:3382-3388 <c>useMonitorBounds = IsWindowedPopup()</c>). Win32 backs this with
    /// <c>MonitorFromPoint(MONITOR_DEFAULTTONEAREST)</c> + <c>GetMonitorInfoW().rcWork</c>; headless returns the
    /// configurable <c>WorkArea</c>. Default: unbounded (no monitor information available).
    /// </summary>
    RectF GetWorkArea(Point2 screenPointPx) => RectF.Infinite;

    /// <summary>
    /// Create a top-level POPUP window (out-of-bounds overlay surface) owned by <see cref="PopupWindowDesc.Owner"/> —
    /// the engine analogue of WinUI's windowed <c>CPopup</c> (Popup_Partial.cpp:1019 <c>SetIsWindowed</c> creates an
    /// HWND via PopupSiteBridge so a flyout can render OUTSIDE the XAML window). Win32: a
    /// <c>WS_POPUP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP</c> owned window that never takes
    /// activation (focus stays on the main window) and forwards its mouse input to the owner; headless: a recorder.
    /// Returns null when the platform cannot create popup windows (callers fall back to root-bounds-constrained
    /// placement — exactly WinUI's <c>CPopup::DoesPlatformSupportWindowedPopup</c> gate, FlyoutBase_Partial.cpp:3188).
    /// </summary>
    IPlatformPopupWindow? CreatePopupWindow(in PopupWindowDesc desc) => null;
}

/// <summary>Host-requested material for a popup HWND. <see cref="TransientAcrylic"/> maps to WinUI's desktop acrylic
/// system backdrop path for windowed MenuFlyout popups; transparent swapchain pixels reveal the OS material.</summary>
public enum PopupWindowMaterial : byte { None = 0, TransientAcrylic = 1 }

/// <summary>Creation parameters for a popup window. <paramref name="Owner"/> = the owning top-level window (the popup
/// stays above it in z-order and never takes activation); <paramref name="BoundsPx"/> = initial bounds in physical
/// virtual-screen px (may be empty — set real bounds via <see cref="IPlatformPopupWindow.SetBoundsPx"/> before Show).</summary>
public readonly record struct PopupWindowDesc(NativeHandle Owner, RectF BoundsPx,
    PopupWindowMaterial Material = PopupWindowMaterial.None, bool Dark = true);

/// <summary>
/// A borderless, non-activating top-level popup surface (the PAL seam for WinUI windowed popups / E4 out-of-bounds
/// flyouts; later the substrate for E10 tear-out windows). The host owns rendering into it (its own swapchain on
/// <see cref="Handle"/>); the popup never owns engine state. All bounds are physical virtual-screen px.
/// </summary>
public interface IPlatformPopupWindow : IDisposable
{
    NativeHandle Handle { get; }

    /// <summary>Last bounds set via <see cref="SetBoundsPx"/> (physical virtual-screen px).</summary>
    RectF BoundsPx { get; }

    /// <summary>Visible (a <see cref="Show"/> not yet followed by <see cref="Hide"/>/<see cref="IDisposable.Dispose"/>).</summary>
    bool IsShown { get; }

    /// <summary>Move/size the popup (physical virtual-screen px) WITHOUT activating it.</summary>
    void SetBoundsPx(in RectF px);

    /// <summary>Show without activating (Win32 <c>SW_SHOWNOACTIVATE</c>) — focus stays on the owner window.</summary>
    void Show();

    void Hide();
}

/// <summary><paramref name="Composited"/> = the window is composited with per-pixel alpha (WS_EX_NOREDIRECTIONBITMAP) so a
/// DirectComposition swapchain can show the DWM Mica backdrop through transparent pixels. <paramref name="CustomFrame"/> =
/// the engine draws the ENTIRE titlebar (WinUI ExtendsContentIntoTitleBar): the platform strips the OS caption
/// (WM_NCCALCSIZE) but keeps the resize frame/shadow, answers WM_NCHITTEST from the engine-reported
/// <see cref="TitleBarRegion"/>s, and synthesizes pointer input for the engine-drawn caption buttons.</summary>
public readonly record struct WindowDesc(string Title, Size2 SizePx, float Scale, bool Composited = false, bool CustomFrame = false);

public interface IPlatformWindow : IDisposable
{
    NativeHandle Handle { get; }
    Size2 ClientSizePx { get; }
    float Scale { get; }

    /// <summary>The screen position of the client area's (0,0), in physical virtual-screen px — the window-DIP →
    /// screen-px bridge for popup-window placement and per-monitor work-area queries (Win32 <c>ClientToScreen</c>;
    /// headless: settable, default (0,0)).</summary>
    Point2 ClientOriginPx => default;

    /// <summary>Drain queued OS input/window events into the ring (once per frame).</summary>
    int PumpInto(InputEventRing ring);

    /// <summary>
    /// Block until platform work arrives or <paramref name="timeoutMs"/> elapses. Negative timeout means wait indefinitely.
    /// Real windows use this for event-driven idle; headless implementations may return immediately.
    /// </summary>
    void WaitForWork(int timeoutMs);

    /// <summary>
    /// Break an in-progress <see cref="WaitForWork"/> from ANY thread so the loop runs another frame promptly — the
    /// thread-safe wake the engine's cross-thread UI dispatch (<c>AppHost.Post</c>) needs. Unlike the host's internal
    /// <c>WakeFrame</c> (UI-thread-only), this is callable from a worker/COM thread: a background producer enqueues a
    /// UI-thread action and calls <see cref="Wake"/> so an idle, fully-blocked loop wakes to drain it. Win32 posts a
    /// benign <c>WM_NULL</c> (PostMessage is thread-safe); headless and other non-blocking backends no-op (their
    /// <see cref="WaitForWork"/> already returns immediately, so the next loop iteration drains the post anyway).
    /// </summary>
    void Wake() { }

    /// <summary>
    /// Invoked by the platform when the OS demands an immediate repaint *outside* the app's frame loop —
    /// notably during the modal move/size loop (WM_SIZE/WM_PAINT), which otherwise blocks rendering until mouse-up.
    /// The host wires this to a pump-free paint so the window stays live during a live resize.
    /// </summary>
    Action? PaintRequested { get; set; }

    /// <summary>True while the OS modal move/size loop is active (between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE): the
    /// app's own frame loop is suspended and only WndProc-driven keep-alive paints run. The host uses this to suppress
    /// REDUNDANT (non-resize) keep-alive paints during a drag — an ambient animation (playback, caret) repainting the
    /// unchanged content every 8 ms timer tick otherwise floods the WndProc thread and starves the modal loop, which is
    /// felt as sluggish, low-fps resizing. Default false (standard frame / headless never enter the loop).</summary>
    bool InModalLoop => false;

    /// <summary>True when the window's pixels are a DComp flip surface (WS_EX_NOREDIRECTIONBITMAP). Composited windows
    /// defer GPU resize + relayout to mouse-up during a modal edge-drag; non-composited windows live-paint (throttled).</summary>
    bool Composited => false;

    /// <summary>True once the current modal loop has delivered WM_SIZE (edge resize, not pure titlebar move).</summary>
    bool SizedInModalLoop => false;

    void SetCursor(CursorId id);                                   // L10 cursor seam
    void SetTitle(StringId title);
    void Show();

    /// <summary>The per-window IME/text-services seam (composition events, candidate-window placement).</summary>
    IPlatformTextInput TextInput { get; }

    // ── custom-titlebar seam (WindowDesc.CustomFrame; defaults are no-ops so standard-frame backends ignore it) ──────

    /// <summary>Push the titlebar's drag/caption-button regions (CLIENT DIP; see <see cref="TitleBarRegion"/>). The
    /// engine calls this only when the titlebar relayouts — push-on-change, never per frame (zero-alloc steady path).
    /// Anything not covered stays HTCLIENT (the bar's interactive content). An empty span clears all regions.</summary>
    void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) { }

    /// <summary>Current placement (drives the custom max↔restore glyph). Change is signaled via
    /// <see cref="InputKind.WindowStateChanged"/>; this property is the pull side.</summary>
    WindowState State => WindowState.Normal;

    /// <summary>True while the window has activation (drives titlebar dimming). Change is signaled via the existing
    /// <see cref="InputKind.WindowFocus"/>/<see cref="InputKind.WindowBlur"/> events; this property is the pull side.</summary>
    bool IsActive => true;

    /// <summary>Engine caption-button commands (Win32: WM_SYSCOMMAND SC_MINIMIZE / SC_MAXIMIZE↔SC_RESTORE / WM_CLOSE).</summary>
    void Minimize() { }
    void ToggleMaximize() { }
    void CloseWindow() { }
}

/// <summary>Versioned external-store-shaped locale seam (modeled on ISystemColors). L9.</summary>
public interface IPlatformLocale
{
    uint Epoch { get; }
}
