using FluentGpu.Foundation;

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
}

/// <summary>
/// POD input event drained from the host-owned ring once per frame (no C# events across the seam).
/// <paramref name="ScrollDelta"/> (Wheel only) is in DIP, oriented so positive = scroll toward the content end
/// (offset increases). The platform pump converts WM_MOUSEWHEEL notches → DIP and flips the sign there.
/// <paramref name="Button"/>: 0 = left, 1 = right, 2 = middle. <paramref name="Mods"/> is the modifier chord at the
/// time of the event (pump-captured); <paramref name="IsRepeat"/> = keyboard auto-repeat (lParam bit 30);
/// <paramref name="TimestampMs"/> = the platform message time (drives double/triple-click detection in the dispatcher).
/// </summary>
public readonly record struct InputEvent(
    InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0f,
    KeyModifiers Mods = KeyModifiers.None, PointerKind Pointer = PointerKind.Mouse,
    bool IsRepeat = false, uint TimestampMs = 0);

/// <summary>Drained by the host each frame; the window writes POD events into it (move-coalesced).</summary>
public sealed class InputEventRing
{
    private InputEvent[] _buf = new InputEvent[64];
    private int _count;

    public void Write(in InputEvent e)
    {
        if (_count == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        _buf[_count++] = e;
    }

    public ReadOnlySpan<InputEvent> Drain()
    {
        var span = _buf.AsSpan(0, _count);
        return span;
    }

    public void Clear() => _count = 0;
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

/// <summary>Creation parameters for a popup window. <paramref name="Owner"/> = the owning top-level window (the popup
/// stays above it in z-order and never takes activation); <paramref name="BoundsPx"/> = initial bounds in physical
/// virtual-screen px (may be empty — set real bounds via <see cref="IPlatformPopupWindow.SetBoundsPx"/> before Show).</summary>
public readonly record struct PopupWindowDesc(NativeHandle Owner, RectF BoundsPx);

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

/// <summary><paramref name="Composited"/> = the window is composited with per-pixel alpha (WS_EX_NOREDIRECTIONBITMAP) so a DirectComposition swapchain can show the DWM Mica backdrop through transparent pixels.</summary>
public readonly record struct WindowDesc(string Title, Size2 SizePx, float Scale, bool Composited = false);

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
    /// Invoked by the platform when the OS demands an immediate repaint *outside* the app's frame loop —
    /// notably during the modal move/size loop (WM_SIZE/WM_PAINT), which otherwise blocks rendering until mouse-up.
    /// The host wires this to a pump-free paint so the window stays live during a live resize.
    /// </summary>
    Action? PaintRequested { get; set; }

    void SetCursor(CursorId id);                                   // L10 cursor seam
    void SetTitle(StringId title);
    void Show();

    /// <summary>The per-window IME/text-services seam (composition events, candidate-window placement).</summary>
    IPlatformTextInput TextInput { get; }
}

/// <summary>Versioned external-store-shaped locale seam (modeled on ISystemColors). L9.</summary>
public interface IPlatformLocale
{
    uint Epoch { get; }
}
