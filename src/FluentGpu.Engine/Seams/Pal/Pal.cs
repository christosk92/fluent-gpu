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
    PointerMove = 1, PointerDown = 2, PointerUp = 3, Key = 4,
    /// <summary>A scroll input (scroll rework §4): the event's <see cref="InputEvent.Scroll"/> carries the ONE
    /// <see cref="FluentGpu.Scroll.Runtime.ScrollInputEvent"/> shape for every producer — a detented wheel notch, a hi-res
    /// wheel packet, a touchpad contact Begin/Sample/End (DirectManipulation), a scripted headless contact. Never
    /// coalesces (each notch/sample authors its own plan at its own device time). A real window delivers wheel
    /// notches SYNCHRONOUSLY through <see cref="IPlatformWindow.SetScrollInputSink"/> as well (wheel is urgent —
    /// never deferred behind a frame); the ring path is for producers pumped once per frame.</summary>
    Scroll = 5, Char = 6,
    KeyUp = 7,
    /// <summary>The platform cancelled an in-flight pointer interaction (capture lost, touch cancel).</summary>
    PointerCancel = 8,
    /// <summary>The window lost activation (WM_ACTIVATE WA_INACTIVE) — light-dismiss overlays close, pressed state clears.</summary>
    WindowBlur = 9,
    WindowFocus = 10,
    /// <summary>The window's placement changed (normal ↔ maximized/minimized) — a custom titlebar re-glyphs max↔restore.</summary>
    WindowStateChanged = 11,

    /// <summary>An OS move/size modal loop of this window ended (Win32 <c>WM_EXITSIZEMOVE</c>) — EVERY loop, edge resizes
    /// included. Consumers that started one (on <see cref="WindowMoveSizeBegan"/>) use it as the gesture's end; others
    /// ignore it. Never coalesces.</summary>
    WindowMoveSizeEnded = 15,
    /// <summary>An OS move/size modal loop of this window BEGAN (Win32 <c>WM_ENTERSIZEMOVE</c>) — EVERY loop, edge resizes
    /// included. Content starts one only through a <see cref="TitleBarHit.Caption"/> region (a press that begins as
    /// client pointer input can never be handed to the OS loop under mouse-in-pointer — measured 2026-09-22). Consumers
    /// that hold chrome for a move start the hold here and release it on <see cref="WindowMoveSizeEnded"/>. Never coalesces.</summary>
    WindowMoveSizeBegan = 16,
}


/// <summary>
/// Flags on <see cref="FrameClock"/>.
/// </summary>
[Flags]
public enum FrameClockFlags : byte
{
    None = 0,
    /// <summary><see cref="FrameClock.FrameQpc"/> IS a compositor tick's vblank instant (the frame was produced for a
    /// live, fresh display-clock tick) — as opposed to the `now` fallback (no clock, or a tick left stale by an idle
    /// stretch). Mirrors <c>RefreshLattice.Build</c>'s decision.</summary>
    LatticeValid = 1,
    /// <summary>No display clock: the platform has no usable compositor clock (headless; a remote session where the
    /// runtime probe ruled the export out), so production is wall-clock paced. While set, a frame-aligned producer's
    /// lead time is floored to 0.</summary>
    Unpaced = 2,
    /// <summary>Produced by the headless PAL (<c>RefreshLattice.Headless</c>): no real present/vblank exists,
    /// <see cref="FrameClock.FrameQpc"/> is the deterministic <c>FixedFrameTimeSource</c> accumulator instead of a
    /// QPC read, so gates stay bit-reproducible.</summary>
    Headless = 4,
}

/// <summary>
/// ONE target time for the frame about to be produced — shared by DirectManipulation's per-frame <c>Update</c>
/// (<see cref="IPlatformWindow.PumpScroll"/>) and the scroll frame step (<c>AppHost.RunScrollFrame</c> evaluates every
/// viewport's plan at <see cref="PresentQpc"/>). Built ONCE per <c>AppHost.RunFrame</c>, at the top, before the input
/// pump/dispatch.
///
/// <para><see cref="FrameQpc"/> is "now", LATTICE-SNAPPED: the nearest point on <c>anchor + k·refresh</c> to the raw
/// QPC read, monotone frame-to-frame (never rewinds — a snap that would land before the previous frame's value is
/// clamped forward). Snapping removes per-frame sampling jitter without shifting any consumer's effective latency
/// (nearest, not next — a zero-mean correction).</para>
///
/// <para><see cref="PresentQpc"/> is the PREDICTED vblank this frame's pixels will actually land on — not the next
/// vblank, but the one AFTER it: the swapchain is created with <c>SetMaximumFrameLatency(1)</c>
/// (<c>D3D12Device.cs</c>), so a frame produced right after the present-ack for frame N-1 shows at the vblank after
/// next, not the next one. <c>PresentQpc / Frequency</c> is the time every scroll plan is evaluated at on the UI thread
/// (the render thread re-derives the same law per compositor tick) and feeds DirectManipulation's contact lead
/// (<c>CompositionDeltaMs</c>).</para>
///
/// <para><see cref="RefreshQpc"/> is the display's measured frame period — DXGI/DWM <c>qpcRefreshPeriod</c>
/// (<see cref="FluentGpu.Rhi.PresentStats.RefreshPeriodQpc"/>) when attested, else <c>Stopwatch.Frequency / 60</c>.
/// Substituted for a body's per-frame <c>DtSec</c> on the FIRST tick after it wakes — kills the zero-dt dead zone a
/// raw "now minus last-now" delta would hit on a body's very first sample.</para>
///
/// <para><see cref="NowQpc"/> is the raw, unsnapped <c>Stopwatch.GetTimestamp()</c> this clock was built from —
/// diagnostics only (the lattice skew a consumer reports IS <c>FrameQpc − NowQpc</c>); nothing in the scroll/render
/// path should read it for physics.</para>
///
/// <para><see cref="Seq"/> is a per-<c>RunFrame</c> monotonic ordinal (never resets), so a consumer that observes two
/// clocks a frame apart can tell "the very next frame" from "we skipped some" without comparing ticks.</para>
///
/// Provenance: DXGI <c>SyncQPCTime</c> is documented as sharing the QPC domain with <c>Stopwatch.GetTimestamp()</c>
/// on Windows (no unit conversion needed to join the two); the "shows two vblanks out under
/// <c>MaximumFrameLatency=1</c>" shape mirrors WinUI/XAML's own frame-info-to-composition-target reasoning.
/// </summary>
public readonly record struct FrameClock(long FrameQpc, long PresentQpc, long RefreshQpc, long NowQpc, ulong Seq, FrameClockFlags Flags);

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
/// <paramref name="Scroll"/> (Kind == <see cref="InputKind.Scroll"/> only) is the scroll input — see
/// <see cref="FluentGpu.Scroll.Runtime.ScrollInputEvent"/>: source, gesture phase, device QPC, pointer DIP and the
/// deltas (notch units for a Notch, DIP for a Sample; positive = toward the content end).
/// <paramref name="QpcTicks"/> is the per-packet high-resolution stamp (POINTER_INFO.PerformanceCount; ticks of
/// <see cref="SystemParams.QpcFrequency"/>; 0 = unavailable → millisecond fallback) feeding the release-velocity
/// estimator. <paramref name="Button"/>: 0 = left, 1 = right, 2 = middle. <paramref name="Mods"/> is the modifier
/// chord at the time of the event (pump-captured); <paramref name="IsRepeat"/> = keyboard auto-repeat (lParam bit 30);
/// <paramref name="TimestampMs"/> = the platform message time (drives double/triple-click detection in the dispatcher).
/// <paramref name="PointerId"/> identifies the contact (mouse = 0; touch/pen carry the OS pointer id) so the ring
/// coalesces moves and the dispatcher captures per contact; <paramref name="Pressure"/> is the normalized contact
/// pressure (mouse = 1; touch/pen report 0..1). WinUI: PointerInputProcessor.cpp / GetPointerInfo POINTER_INFO.
/// </summary>
public readonly record struct InputEvent(
    InputKind Kind, Point2 PositionPx, int Button, int KeyCode,
    KeyModifiers Mods = KeyModifiers.None, PointerKind Pointer = PointerKind.Mouse,
    bool IsRepeat = false, uint TimestampMs = 0, uint PointerId = 0, float Pressure = 1f,
    long QpcTicks = 0,          // per-packet high-res stamp (SystemParams.QpcFrequency ticks; 0 = ms fallback)
    FluentGpu.Scroll.Runtime.ScrollInputEvent Scroll = default)
{
    /// <summary>A scroll input event wrapped for the ring (<see cref="InputKind.Scroll"/>).</summary>
    public static InputEvent ForScroll(in FluentGpu.Scroll.Runtime.ScrollInputEvent scroll, PointerKind pointer = PointerKind.Mouse, uint timestampMs = 0)
        => new(InputKind.Scroll, scroll.PointerDip, 0, 0, scroll.Mods, pointer, false, timestampMs, scroll.PointerId, 1f, scroll.Qpc, scroll);
}

/// <summary>One pre-coalesce touch/pen velocity sample: the per-frame event coalescing keeps only the newest move,
/// which would cap release-velocity fidelity at frame resolution — so every coalesced-away touch/pen move deposits its
/// absolute DIP <c>(position, stamp)</c> here instead. The dispatcher drains this alongside the events and feeds the
/// impulse estimator; feeding is idempotent (the estimator rejects non-monotonic stamps).</summary>
public readonly record struct PointerVelSample(uint PointerId, float X, float Y, uint TimestampMs, long QpcTicks);

/// <summary>
/// Drained by the host each frame (drain-to-empty, single contiguous span — <c>AppHost.RunFrame</c> Clears, the window
/// writes, then the dispatcher consumes the whole <see cref="Drain"/> span). Fixed-capacity slab: never allocates after
/// construction. A <see cref="InputKind.PointerMove"/> whose previous unconsumed move for the SAME <see cref="InputEvent.PointerId"/>
/// is still in the slab overwrites it in place (the dispatcher only needs the latest position per contact between frames —
/// WinUI's <c>GetPointerFrameInfoHistory</c> OS-side coalescing); Down/Up/Key/Char/Cancel/Scroll never coalesce (a
/// scroll notch or contact sample authors its own plan at its own device time). On slab overflow of a non-coalescible event the OLDEST
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

    /// <summary>Number of events currently retained after coalescing.</summary>
    public int Count => _count;

    public void Write(in InputEvent e)
    {
        if (e.Kind == InputKind.PointerMove)
        {
            // Deposit EVERY touch/pen move into the velocity side ring, not just
            // the one that gets coalesced away: the coalesced slab keeps only the newest position per contact (for
            // hit-test/hover), but the release-velocity estimator needs the FULL chronological packet stream —
            // depositing only the overwritten sample dropped the LAST move of every frame (it is never overwritten by
            // a later one in the same frame). Deposited BEFORE the coalesce decision so ordering matches arrival.
            if (e.Pointer is PointerKind.Touch or PointerKind.Pen)
                PushVelocitySample(new PointerVelSample(e.PointerId, e.PositionPx.X, e.PositionPx.Y, e.TimestampMs, e.QpcTicks));

            int slot = IdSlot(e.PointerId);
            if (slot >= 0 && _lastMove[slot] >= 0)
            {
                // Coalesce: overwrite this id's pending move in place (hit-test/hover only needs the latest position
                // per contact between frames) — its velocity contribution was already deposited above.
                _buf[_lastMove[slot]] = e;
                return;
            }
            int idx = Append(in e);
            if (slot >= 0) _lastMove[slot] = idx;
            return;
        }

        // A non-move event is an ordering barrier. A later move must never overwrite a move that precedes a
        // Down/Up/Cancel/key/window event in the same pump (Move A, Up, Move B must drain in that order). Keep the
        // fixed id map, but invalidate every pending-move index so the next move appends after the barrier.
        for (int i = 0; i < IdSlots; i++) _lastMove[i] = -1;

        Append(in e);   // Down/Up/Key/Char/Cancel/window/scroll events: never coalesce
    }

    // ── the velocity side ring (design §2) ────────────────────────────────────────────────────────────────────────
    private const int VelCapacity = 128;  // ≥4× headroom over a 1 kHz device at 60 Hz frames (≈16 samples/frame); raised
                                           // from 64 for the every-move deposit rule above (every touch/pen PointerMove
                                           // now deposits, not just the coalesced-away ones).
    private readonly PointerVelSample[] _vel = new PointerVelSample[VelCapacity];
    private int _velCount;

    /// <summary>Producers (and the ring's own coalescing) deposit pre-coalesce samples here. Overflow drops the
    /// OLDEST (velocity is a trailing estimate — the newest samples carry it); one shift on a rare path, zero growth.</summary>
    public void PushVelocitySample(in PointerVelSample s)
    {
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

    /// <summary>Move all retained samples/events into <paramref name="destination"/> in chronological order, then
    /// clear this ring. Used by platform backends to coalesce high-rate native input before the host's frame pump.</summary>
    public int MoveTo(InputEventRing destination)
    {
        int n = _count;
        for (int i = 0; i < _velCount; i++) destination.PushVelocitySample(in _vel[i]);
        for (int i = 0; i < _count; i++) destination.Write(in _buf[i]);
        Clear();
        return n;
    }

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
    /// Raised when the user clicks a taskbar thumbnail-toolbar button (<c>ITaskbarList3.ThumbBarAddButtons</c>). The
    /// payload is the button's application-defined id (<c>LOWORD(wParam)</c> of <c>WM_COMMAND</c> /
    /// <c>THBN_CLICKED</c>) — a plain <see cref="int"/> so this seam stays TerraFX-free. The buttons themselves are
    /// produced outside the PAL by <c>FluentGpu.WindowsApi.Shell.TaskbarManager</c>.
    /// <para>
    /// THREADING CONTRACT — delivered on the UI thread. The Win32 backend raises it synchronously from
    /// <c>WM_COMMAND</c> (OS-dispatched on the window's own thread), so subscribers may touch non-thread-safe host
    /// state (e.g. <c>AppHost.WakeFrame</c>) directly. Default implementation never fires (headless / non-Windows
    /// backends). Same stash/drain discipline as <see cref="ActivationRedirected"/>: <c>AppHost</c> stashes the id and
    /// re-raises at the top of <c>Paint</c>.
    /// </para>
    /// </summary>
    event Action<int>? ThumbButtonClicked { add { } remove { } }

    /// <summary>
    /// Raised when the OS reports a browser-style navigation command — a mouse's side buttons (XButton1/2) or a keyboard
    /// Back/Forward key. The payload is <c>0 = Back</c>, <c>1 = Forward</c>: a plain <see cref="int"/> so this seam stays
    /// TerraFX-free, and deliberately NOT a pointer button, because those buttons are not a click at a position — the OS
    /// delivers them as a command (Win32 <c>WM_APPCOMMAND</c>) and an app is expected to act on them globally.
    /// <para>
    /// THREADING CONTRACT — delivered on the UI thread, same stash/drain discipline as <see cref="ThumbButtonClicked"/>
    /// (<c>AppHost</c> stashes and re-raises at the top of <c>Paint</c>). Default implementation never fires (headless /
    /// non-Windows backends; macOS has no equivalent side-button command and is expected to leave this silent).
    /// </para>
    /// </summary>
    event Action<int>? AppNavigationCommand { add { } remove { } }

    /// <summary>
    /// Raised when explorer creates (or re-creates) this window's taskbar button — the registered
    /// <c>TaskbarButtonCreated</c> window message. <c>ITaskbarList3.ThumbBarAddButtons</c> is only legal after this;
    /// explorer also re-broadcasts it after a shell restart, which discards any previously added thumbnail toolbar.
    /// Default implementation never fires. Same UI-thread + stash/drain discipline as <see cref="SystemColorsChanged"/>.
    /// </summary>
    event Action? TaskbarButtonCreated { add { } remove { } }

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
/// <see cref="TitleBarRegion"/>s, and synthesizes pointer input for the engine-drawn caption buttons.
/// <paramref name="MinClientSizeDip"/> is an opt-in minimum tracking size in logical DIP; an empty axis leaves that
/// axis at the platform default. <paramref name="Zoom"/> is the initial browser-style app zoom (typically a persisted
/// user preference — see <see cref="IPlatformWindow.SetZoom"/>): it folds into the effective
/// <see cref="IPlatformWindow.Scale"/> and shrinks/grows the DIP viewport WITHOUT changing the physical window size.
/// Sanitized through <see cref="ZoomLadder.Clamp"/> by the backend.
/// <paramref name="SkipDropAndTouchpad"/> = do not register the window as an OS file-drop target and do not create the
/// DirectManipulation touchpad producer (a few ms of COM/OLE activation per window). For a window that never takes a file drop and
/// has no touchpad-scrolled content, such as the video pop-out; the touchpad then scrolls through the wheel fallback. Default false.</summary>
public readonly record struct WindowDesc(
    string Title,
    Size2 SizePx,
    float Scale,
    bool Composited = false,
    bool CustomFrame = false,
    Size2 MinClientSizeDip = default,
    float Zoom = 1f,
    bool SkipDropAndTouchpad = false);

/// <summary>How native input should affect a bounded platform wait.</summary>
public enum PlatformInputWakePolicy : byte
{
    /// <summary>Return as soon as any platform message arrives.</summary>
    Immediate = 0,
    /// <summary>Consume/coalesce pointer-motion messages until the existing absolute wait deadline; all other input
    /// remains urgent and breaks the wait immediately.</summary>
    CoalescePointerMotion = 1,
}

/// <summary>A snapshot of the platform display clock (see <see cref="IPlatformWindow.DisplayClock"/>): <paramref name="TickSeq"/>
/// increments once per compositor tick (vblank) while armed; <paramref name="TickQpc"/> is the <c>Stopwatch</c>-domain
/// instant of that tick. <paramref name="Available"/> false ⇒ no compositor clock (the host software-paces).</summary>
public readonly record struct DisplayClockSample(bool Available, long TickSeq, long TickQpc,
    long MeasuredPeriodQpc = 0, long IgnoredReturns = 0, long SlotDrops = 0, bool Decimating = false);

/// <summary>One host-to-platform wait request. Negative timeout means wait indefinitely.
///
/// <paramref name="WakeOnDisplayClock"/> asks the backend to end the wait on the DISPLAY's clock (the compositor tick —
/// the vblank) as well as on its usual sources. It is the production pacer of the async render path: while it is set the
/// backend keeps its display clock armed across frames (ticks are counted even while the host is producing, so a tick
/// that lands mid-frame ends the NEXT wait immediately instead of being missed), and the host produces at most one frame
/// per tick (<see cref="DisplayClockSample"/>). A backend without a compositor clock (headless, a remote session where
/// the probe fails) ignores it and the wall-clock timeout paces the loop.</summary>
public readonly record struct PlatformWaitRequest(
    int TimeoutMs,
    PlatformInputWakePolicy InputWakePolicy = PlatformInputWakePolicy.Immediate,
    bool WakeOnDisplayClock = false);

/// <summary>
/// Independent render-consumer subscription to the platform display clock. Create on the UI thread, then arm/wait
/// on the render thread. UI idling cannot consume or disarm its ticks. Disposal follows render-thread join.
/// A capability failure wakes the subscriber once; the renderer then uses its bounded software-paced fallback.
/// </summary>
public interface IRenderDisplayClock : IDisposable
{
    System.Threading.WaitHandle Tick { get; }
    bool IsAvailable { get; }
    void SetActive(bool active);

    /// <summary>Monotone count of ticks the underlying clock has DELIVERED (not merely observed) — the render thread's
    /// "which vblank is this" guard against presenting twice for the same tick (a wake turn followed by a still-
    /// signalled tick event landing in the same vblank). 0 for a backend that cannot say (⇒ the render thread falls
    /// back to its no-display-clock behaviour: never gate on it). Render-thread read only.</summary>
    long TickSeq => 0;

    /// <summary>QPC stamp of the tick <see cref="TickSeq"/> names (0 for a backend that cannot say). Diagnostics only —
    /// the render thread's <c>[render.pace]</c> tick→present lag; never a pacer. Render-thread read only.</summary>
    long TickQpc => 0;

    /// <summary>The clock filter's measured compositor beat (QPC ticks; 0 until measured / a backend that cannot say).
    /// Diagnostics only — the <c>[render.pace]</c> line and the pace snapshot.</summary>
    long MeasuredPeriodQpc => 0;

    /// <summary>Returns the filter swallowed as double ticks (cumulative). Diagnostics only.</summary>
    long IgnoredReturns => 0;

    /// <summary>Accepted ticks dropped between window slots while decimating onto a slower window (cumulative).
    /// Diagnostics only.</summary>
    long SlotDrops => 0;

    /// <summary>The clock is decimating onto a window slower than the compositor beat. Diagnostics only.</summary>
    bool Decimating => false;
}

public interface IPlatformWindow : IDisposable
{
    /// <summary>Optional independent render clock subscription; no clock thread is required of headless backends.</summary>
    IRenderDisplayClock? CreateRenderDisplayClock() => null;

    NativeHandle Handle { get; }
    Size2 ClientSizePx { get; }

    /// <summary>The EFFECTIVE scale (px per engine DIP): the OS per-monitor DPI scale × the app <see cref="Zoom"/>.
    /// Everything downstream — the layout DIP viewport, <c>FrameInfo.Scale</c>, glyph raster, damage, popup placement,
    /// IME and input px↔DIP conversion — consumes this one product; the host's per-frame <c>EnsureSize</c> treats any
    /// change (a monitor hop OR a zoom step) as a full-relayout event.</summary>
    float Scale { get; }

    /// <summary>The browser-style app zoom factor folded into <see cref="Scale"/> (1f = 100%). Discrete — the ladder
    /// and its rationale live in <see cref="ZoomLadder"/>. Default: no zoom (backends without zoom support report 1f).</summary>
    float Zoom => 1f;

    /// <summary>Set the app zoom: the backend clamps via <see cref="ZoomLadder.Clamp"/>, folds it into the effective
    /// <see cref="Scale"/>, and requests a paint — the host's per-frame <c>EnsureSize</c> then sees the Scale change
    /// and re-lays-out in the new DIP viewport (the same path as a per-monitor DPI hop). The physical window size is
    /// untouched (browser behavior: content scales, the window stays). Default: a no-op (no zoom support).</summary>
    void SetZoom(float zoom) { }

    /// <summary>The screen position of the client area's (0,0), in physical virtual-screen px — the window-DIP →
    /// screen-px bridge for popup-window placement and per-monitor work-area queries (Win32 <c>ClientToScreen</c>;
    /// headless: settable, default (0,0)).</summary>
    Point2 ClientOriginPx => default;

    /// <summary>The window's OUTER rect in physical virtual-screen px (Win32 <c>GetWindowRect</c>), or an empty rect when
    /// the backend cannot report it. This is the read side of <see cref="SetBoundsPx"/>: a host that remembers where the
    /// user put a secondary window needs to ask where it ENDED UP, which client origin + client size cannot answer for a
    /// window with OS chrome.</summary>
    RectF OuterBoundsPx => default;

    /// <summary>Drain queued OS input/window events into the ring (once per frame).</summary>
    int PumpInto(InputEventRing ring);

    /// <summary>
    /// Called from <c>AppHost.Paint</c> AFTER the display-phase gate has decided this frame is actually going to be
    /// produced, ONCE per PRODUCED frame: a frame-aligned contact producer (DirectManipulation's touchpad contact
    /// stream on Windows) issues its ONE <c>Update</c> for <paramref name="clock"/> here and enqueues this frame's
    /// <see cref="InputKind.Scroll"/> Begin/Sample/End events into <paramref name="ring"/> — the same shape
    /// <see cref="PumpInto"/> produces, so the host dispatches the returned span through the ordinary input path.
    /// Returns the number of events written (0 = nothing pending). Default: a no-op.
    /// </summary>
    int PumpScroll(in FrameClock clock, InputEventRing ring) => 0;

    /// <summary>Installs the host's URGENT scroll sink (scroll rework §1 #3 / §4): a real window invokes it
    /// synchronously, on the UI thread, from the wheel message itself — the notch authors its plan and writes the plan
    /// table without waiting for a frame. Backends without a synchronous wheel path (headless) ignore it and deliver
    /// scroll through the ring like every other event. Pass null to detach.</summary>
    void SetScrollInputSink(Action<FluentGpu.Scroll.Runtime.ScrollInputEvent>? sink) { }

    /// <summary>
    /// True while a frame-aligned scroll producer has a contact engaged or pending (DirectManipulation SetContact
    /// issued but not yet RUNNING, or already RUNNING) OR a hi-res wheel gesture is live — in every such case the
    /// host MUST produce one frame per refresh so <see cref="PumpScroll"/> gets called every vblank. Folded into the
    /// frame loop's wake/idle decision (<c>AppHost.ComputeWakeReasons</c>,
    /// <see cref="FluentGpu.Hosting.WakeReasons.ScrollProducer"/>). Default false.
    /// </summary>
    bool ScrollProducerLive => false;

    /// <summary>
    /// Block until platform work arrives or <paramref name="timeoutMs"/> elapses. Negative timeout means wait indefinitely.
    /// Real windows use this for event-driven idle; headless implementations may return immediately.
    /// </summary>
    void WaitForWork(int timeoutMs);

    /// <summary>Typed wait request used by display-paced hosts. Backends that do not support input-aware pacing retain
    /// the ordinary <see cref="WaitForWork(int)"/> behavior through this default implementation.</summary>
    void WaitForWork(in PlatformWaitRequest request) => WaitForWork(request.TimeoutMs);

    /// <summary>
    /// Break an in-progress <see cref="WaitForWork"/> from ANY thread so the loop runs another frame promptly — the
    /// thread-safe wake the engine's cross-thread UI dispatch (<c>AppHost.Post</c>) needs. Unlike the host's internal
    /// <c>WakeFrame</c> (UI-thread-only), this is callable from a worker/COM thread: a background producer enqueues a
    /// UI-thread action and calls <see cref="Wake"/> so an idle, fully-blocked loop wakes to drain it. Win32 signals a
    /// present-ack waitable that <see cref="WaitForWork"/> waits on atomically with input messages (and the
    /// high-resolution timer when armed) AND posts a benign <c>WM_NULL</c>; headless and other non-blocking backends
    /// no-op (their <see cref="WaitForWork"/> already returns immediately, so the next loop iteration drains the post
    /// anyway).
    /// </summary>
    void Wake() { }

    /// <summary>The display clock the host paces production on: the compositor tick sequence and the QPC instant of the
    /// latest tick (a vblank). <see cref="DisplayClockSample.Available"/> is false when the backend has no usable
    /// compositor clock (headless; a remote session where the runtime probe ruled it out) — the host then software-paces.
    /// Ticks are counted while the clock is armed by a <see cref="PlatformWaitRequest.WakeOnDisplayClock"/> wait and
    /// keep counting until the host issues a wait WITHOUT that request (idle/ambient), so a produced frame's tick is
    /// never lost to a wake that landed mid-frame.</summary>
    DisplayClockSample DisplayClock => default;

    /// <summary>The refresh period of the display THIS window is currently on, in Stopwatch ticks; 0 when unknown
    /// (headless, or a backend with no per-monitor query). Higher priority than the device's swapchain
    /// <see cref="FluentGpu.Rhi.PresentStats.RefreshPeriodQpc"/> in <c>AppHost</c>'s refresh-period funnel: a
    /// per-WINDOW source follows that window's own monitor (a drag to a different-rate display, or simply being on a
    /// secondary monitor) with no app involvement, where the device-wide PresentStats reflects only the swapchain's
    /// present history and can go stale across a monitor change. Default 0 defers to the device/60 Hz fallback.</summary>
    long DisplayRefreshPeriodQpc => 0;

    /// <summary>
    /// Invoked by the platform when the OS demands an immediate repaint *outside* the app's frame loop —
    /// notably during the modal move/size loop (WM_SIZE/WM_PAINT), which otherwise blocks rendering until mouse-up.
    /// The host wires this to a pump-free paint so the window stays live during a live resize.
    /// </summary>
    Action? PaintRequested { get; set; }

    /// <summary>Raised by the platform on every keep-alive beat of an OS modal move/size loop THIS window is in (the 8 ms loop
    /// timer, and each WM_SIZE of an edge resize), AFTER the window's own paint decision, whether or not that decision painted.
    /// The loop runs inside a DispatchMessage on the one UI thread, so every other window of the process (the main window while
    /// a pop-out is dragged, the pop-outs while the main window is) would otherwise be frozen until mouse-up (F093): the host
    /// wires this to a throttled, paint-only round over its peers - never a frame loop turn, which must not run from inside
    /// another window's message dispatch. Default: a no-op (a window with no modal loop never raises it).</summary>
    Action? ModalLoopTick { get => null; set { } }

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

    /// <summary>Hide the window without destroying it (Win32 <c>SW_HIDE</c>: no taskbar button, no Alt+Tab entry, the
    /// HWND and every engine resource stay alive). <see cref="Show"/> brings it back in the placement it had. While
    /// hidden the host parks exactly as if minimized (<see cref="WindowStatus.Parked"/>). Default: a no-op (a backend
    /// without window visibility).</summary>
    void Hide() { }

    /// <summary>True while the window is shown. The pull side of <see cref="Show"/>/<see cref="Hide"/>, read by the host
    /// every frame to park a hidden window. Win32 reads the live <c>WS_VISIBLE</c> style, so a window shown or hidden
    /// by any other code path is still seen correctly. Default true.</summary>
    bool IsVisible => true;

    /// <summary>True while the OS compositor CLOAKS the window: it keeps <c>WS_VISIBLE</c> but nothing of it reaches the
    /// screen (Win32 <c>DWMWA_CLOAKED</c>: the window lives on another virtual desktop, or a shell transition is running).
    /// Unlike <see cref="IsVisible"/> this raises no message, so a host that parks on it must poll. A detached child host
    /// parks while this holds (<see cref="CloakParkGate"/>); the primary window does not read it. Default false.</summary>
    bool IsCloaked => false;

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
    /// <summary>True while the client occupies the current monitor with window chrome removed.</summary>
    bool IsFullscreen => false;
    /// <summary>Enter/leave borderless monitor fullscreen, restoring the exact prior window placement on exit.</summary>
    void SetFullscreen(bool fullscreen) { }
    void CloseWindow() { }

    /// <summary>Asked before the window closes (Win32 <c>WM_CLOSE</c>: the caption close button, Alt+F4, the system
    /// menu, <see cref="CloseWindow"/>). Return true to keep the window — the handler has handled the request, typically
    /// by hiding to a notification-area icon; false (or no handler) lets it close. A
    /// <see cref="CloseReason.SessionEnding"/> request cannot be vetoed: the handler is told, then the window closes
    /// (<see cref="WindowCloseGate"/>). Invoked on the UI thread from inside the platform's message dispatch. Default:
    /// no handler and nothing stored (a backend whose windows close unconditionally).</summary>
    Func<CloseReason, bool>? CloseRequested { get => null; set { } }

    /// <summary>True once the window has been closed (its HWND destroyed). The host loop reaps a closed detached window.
    /// Default false (headless / never-closing seams).</summary>
    bool IsClosed => false;

    // ── detached-window seam (a movable/resizable always-on-top secondary window, e.g. the pop-out video mini-player) ──
    // Defaults no-op so headless and single-window backends are unaffected; the Win32 backend implements them.

    /// <summary>Keep this window above all others (Win32 <c>SetWindowPos(HWND_TOPMOST/NOTOPMOST, SWP_NOMOVE|SWP_NOSIZE|
    /// SWP_NOACTIVATE)</c>) — persistent, unlike a one-shot bring-to-front. Used by the pop-out video window's
    /// always-on-top toggle. State-aware callers assert it only while there is video worth watching.</summary>
    void SetTopmost(bool topmost) { }

    /// <summary>Programmatically move/resize the window in physical virtual-screen px (restore saved geometry, fit to
    /// content). Win32 <c>SetWindowPos(SWP_NOZORDER|SWP_NOACTIVATE)</c>. The rect is the OUTER window rect.</summary>
    void SetBoundsPx(RectF outerBoundsPx) { }

    /// <summary>Programmatically MOVE the window (outer origin, physical virtual-screen px) WITHOUT touching its size —
    /// the pure-move sibling of <see cref="SetBoundsPx"/> (restoring a remembered position without re-deriving the
    /// current size, and without the resize-adjacent side effects a full <see cref="SetBoundsPx"/> call can trigger on
    /// a composited/detached window). Win32 <c>SetWindowPos(SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE)</c>. Default: a
    /// no-op (a backend without window placement).</summary>
    void MoveToPx(Point2 outerOriginPx) { }

    /// <summary>Minimum CLIENT size in physical px (Win32 <c>WM_GETMINMAXINFO</c>). Default <c>0×0</c> = no clamp, so
    /// the primary window is unaffected; a detached mini-player sets a floor.</summary>
    void SetMinClientSizePx(Size2 px) { }

    /// <summary>Tell the window whether it currently composites a LIVE video surface. Pushed by the host at the video
    /// drain, one way, engine → PAL (the same direction as <see cref="SetMinClientSizePx"/>); the PAL only ever reads
    /// a bool and never reaches back for the registry.
    ///
    /// <para>WHY A WINDOW NEEDS TO KNOW. A composited window DEFERS ALL PAINTING for the duration of an OS modal
    /// edge-resize — deliberately, because DWM keeps presenting the last flip surface and repainting mid-loop only
    /// costs frames. But a video child visual is placed from the frame loop, so with zero frames the child keeps its
    /// pre-resize size and position while the window frame moves under it: the picture visibly lags the frame until the
    /// loop ends. A window carrying live video therefore keeps a throttled keep-alive during the resize so the hole and
    /// the child stay together. Default <c>false</c> — the general defer is a real performance win and stays intact
    /// for every window without video.</para></summary>
    void SetHasLiveVideo(bool hasLiveVideo) { }
}

/// <summary>Versioned external-store-shaped locale seam (modeled on ISystemColors). L9.</summary>
public interface IPlatformLocale
{
    uint Epoch { get; }
}
