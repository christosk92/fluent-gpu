using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>Real Win32 PAL: <c>CreateWindowExW</c> + a static <c>WndProc</c> + a PeekMessage pump that drains WM_* into POD input.</summary>
public sealed unsafe partial class Win32App : IPlatformApp
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4 — render at native resolution (no OS bitmap-stretch blur).
    [LibraryImport("user32.dll")]
    private static partial int SetProcessDpiAwarenessContext(nint value);

    // SPI_GETMENUDROPALIGNMENT = 0x001B (WinUser.h) — left-handed "menus drop right-aligned" preference.
    private const uint SpiGetMenuDropAlignment = 0x001B;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, void* pvParam, uint fWinIni);

    public Win32App()
    {
        SetProcessDpiAwarenessContext(unchecked((nint)(-4)));
        ReadSystemParams();
    }

    /// <summary>One-time OS user-preference reads into <see cref="SystemParams"/> — the same sources WinUI consults:
    /// HKCU "Control Panel\Desktop" MenuShowDelay with the 400ms fallback (CascadingMenuHelper.cpp:83-95) and
    /// SPI_GETMENUDROPALIGNMENT handedness (Slider_Partial.cpp:2094-2099).</summary>
    private static void ReadSystemParams()
    {
        try
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", null)
                    is string s && int.TryParse(s, out int delayMs) && delayMs >= 0)
                SystemParams.MenuShowDelayMs = delayMs;
        }
        catch { /* best-effort: keep the 400ms WinUI fallback */ }

        int dropRight = 0;
        if (SystemParametersInfoW(SpiGetMenuDropAlignment, 0, &dropRight, 0))
            SystemParams.MenuDropRightAligned = dropRight != 0;
    }

    public IPlatformWindow CreateWindow(in WindowDesc desc) => new Win32Window(desc);
    public IClipboard Clipboard { get; } = new Win32Clipboard();

    // MONITOR_DEFAULTTONEAREST = 2 (WinUser.h) — the monitor nearest the point, never null.
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Work area (desktop minus taskbar) of the monitor containing the point, in physical virtual-screen px.
    /// The same data WinUI's windowed popups place against (FlyoutBase_Partial.cpp:3382-3388 monitor bounds;
    /// Popup.cpp windowed placement) — <c>MonitorFromPoint(MONITOR_DEFAULTTONEAREST)</c> + <c>GetMonitorInfoW().rcWork</c>.</summary>
    public RectF GetWorkArea(Point2 screenPointPx)
    {
        POINT pt = new() { x = (int)MathF.Round(screenPointPx.X), y = (int)MathF.Round(screenPointPx.Y) };
        HMONITOR mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (mon == HMONITOR.NULL) return RectF.Infinite;
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        if (!GetMonitorInfoW(mon, &mi)) return RectF.Infinite;
        return new RectF(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right - mi.rcWork.left, mi.rcWork.bottom - mi.rcWork.top);
    }

    /// <summary>A WS_POPUP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP owned window — the engine
    /// analogue of WinUI's windowed CPopup HWND (Popup_Partial.cpp:1019 SetIsWindowed). Never activates; mouse input
    /// over it is forwarded to the owner <see cref="Win32Window"/> translated into owner-client DIP.</summary>
    public IPlatformPopupWindow? CreatePopupWindow(in PopupWindowDesc desc)
        => desc.Owner.Kind == NativeHandleKind.Hwnd ? new Win32PopupWindow(desc) : null;

    // ShellExecuteW = the Win32 equivalent of Launcher::LaunchUriAsync: "open" verb → the OS default protocol
    // handler (browser/mailto). Returns an HINSTANCE-alike (>32 = success) — ignored, best-effort like WinUI's
    // TryInvokeLauncher (HyperLinkButton_Partial.cpp:172). nShowCmd 1 = SW_SHOWNORMAL.
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecuteW(nint hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>WinUI HyperlinkButton NavigateUri launch (HyperLinkButton_Partial.cpp:172).</summary>
    public void OpenUri(string uri) => ShellExecuteW(0, "open", uri, null, null, 1);

    // ── single-instance activation redirect (IPlatformApp.ActivationRedirected) ──────────────────────────────────────
    // A second launch of a single-instance app forwards its activation URI to this running instance via WM_COPYDATA
    // (FluentGpu.WindowsApi.Activation.SingleInstanceGate). The message lands in Win32Window.Handle32 (the WM_COPYDATA
    // case), which routes it here through s_activationRedirected. Static bridge because the message arrives on the
    // window (not the app) and the static WndProc has no app instance in hand — the single-window v1 host model means
    // last-constructed app wins, mirroring how AppHost mirrors OpenUri onto InputHooks.Current.Default.
    private static Action<string>? s_activationRedirected;

    /// <inheritdoc/>
    public event Action<string>? ActivationRedirected
    {
        add => s_activationRedirected += value;
        remove => s_activationRedirected -= value;
    }

    /// <summary>Raise <see cref="ActivationRedirected"/> on the UI thread. Called by <see cref="Win32Window"/>'s
    /// WM_COPYDATA case (which the OS dispatches on the window's own thread), so subscribers run UI-thread-safe.</summary>
    internal static void RaiseActivationRedirected(string payload) => s_activationRedirected?.Invoke(payload);

    // ── OS color settings change (IPlatformApp.SystemColorsChanged) ─────────────────────────────────────────────────
    // WM_SETTINGCHANGE("ImmersiveColorSet") lands in Win32Window.Handle32 and routes here (static bridge, same single-
    // window rationale as the activation redirect above). The host re-reads OS dark/accent and re-themes live.
    private static Action? s_systemColorsChanged;

    /// <inheritdoc/>
    public event Action? SystemColorsChanged
    {
        add => s_systemColorsChanged += value;
        remove => s_systemColorsChanged -= value;
    }

    /// <summary>Raise <see cref="SystemColorsChanged"/> on the UI thread (the OS dispatches WM_SETTINGCHANGE on the
    /// window's own thread), so subscribers run UI-thread-safe.</summary>
    internal static void RaiseSystemColorsChanged() => s_systemColorsChanged?.Invoke();

    public void Dispose() { }
}

public sealed unsafe partial class Win32Window : IPlatformWindow
{
    // Win32 ABI constants (stable; defined locally to avoid TerraFX's per-prefix constant classes).
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SIZE = 0x0005,
                       WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
                       WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105,
                       WM_CHAR = 0x0102, WM_ACTIVATE = 0x0006, WM_SETCURSOR = 0x0020, WM_CAPTURECHANGED = 0x0215,
                       WM_COPYDATA = 0x004A,   // single-instance activation redirect (SingleInstanceGate → ActivationRedirected)
                       WM_SETTINGCHANGE = 0x001A,   // OS settings change; lParam names the area ("ImmersiveColorSet" = dark-mode/accent)
                       WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F,
                       WM_IME_SETCONTEXT = 0x0281,
                       // Modal move/size loop keep-alive: DefWindowProc runs its OWN message pump while the user drags the
                       // window, so the app's frame loop is suspended. WM_SIZE already pumps a paint during a live RESIZE;
                       // a pure MOVE fires neither WM_SIZE nor WM_PAINT, so content froze and trailed the cursor. We bracket
                       // the loop (ENTER/EXITSIZEMOVE), repaint per position change (WM_MOVE), and run a timer so animations
                       // stay live even while the mouse is held still mid-drag. Safe only because keep-alive paints no longer
                       // block on vblank (IGpuDevice.SuppressLatencyWaitOnce); a blocking paint here would stall each step.
                       WM_MOVE = 0x0003, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_TIMER = 0x0113,
                       WM_DROPFILES = 0x0233;   // OS file/folder drop (DragAcceptFiles); wParam = HDROP

    private const nuint MoveLoopTimerId = 1;   // SetTimer id for the modal move/size loop keep-alive pump
    private const uint WM_FG_COALESCED_SIZE_PAINT = 0x8000 + 0x4F; // WM_APP-range queued paint for modal live-resize coalescing
    // ── Pointer input (EnableMouseInPointer): the WM_MOUSE* client stream is retired — mouse, touch and pen all arrive
    //    here as WM_POINTER* (PT_MOUSE/PT_TOUCH/PT_PEN/PT_TOUCHPAD). One contact = one PointerId; implicit contact→window capture
    //    replaces the SetCapture refcount. WM_POINTERWHEEL/HWHEEL REPLACE WM_MOUSEWHEEL/HWHEEL once mouse-in-pointer is on.
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247,
                       WM_POINTERLEAVE = 0x024A, WM_POINTERCAPTURECHANGED = 0x024C,
                       WM_POINTERWHEEL = 0x024E, WM_POINTERHWHEEL = 0x024F;
    // ── precision-touchpad raw-delta shaping (FROZEN) ───────────────────────────────────────────────────────────────
    // Empirically dialed in on real precision-touchpad hardware (the `WAS …` notes are the tuning trail) and now FIXED
    // literals — the FG_TP_SCALE/KNEE/MAXRAW env overrides + their startup TryParse were removed once the feel settled.
    // Change a value here and rebuild to retune.
    //
    // Pan distance: DIP of content travel per raw wheel-delta unit (HIWORD of WM_POINTERWHEEL). This is THE base scroll-speed
    // knob — raise it if everyday scrolling feels heavy/laborious, lower it if it feels twitchy. The ShapeTouchpadPacketDelta
    // precision zone (≤18 DIP) keeps small/precise motions linear after this scale, so a higher value speeds normal swipes
    // without making fine adjustments overshoot. The driver emits the momentum tail; the engine smooths it, adds no inertia.
    // WAS 0.08 — on-device + with other testers, normal-speed swipes didn't travel far enough ("too slow / laborious";
    // the fast swipes in the trace looked fine, but you had to swipe HARD). 0.11 (+~38%) lifts the everyday feel.
    private static readonly float s_tpScale = 0.11f;
    // Soft-knee that tames the device's accelerated raw notch BEFORE scaling: small deltas stay EXACTLY linear (precise
    // panning untouched), only the big accelerated packets are compressed toward a soft ceiling — |notch| below s_tpKnee
    // is passed through 1:1, above it the curve asymptotes toward s_tpMaxRaw. Applies ONLY to the hi-res
    // (precision-touchpad) branch; the detented mouse-wheel branch is untouched.
    // WAS 240 — every real packet on this device sat above the old knee, so all scrolling was de-amplified (inverted gain); widened the linear region.
    private static readonly float s_tpKnee = 600f;
    // WAS 1600 — raised the compression ceiling so a hard flick (per-packet DIP cap 128→224) genuinely throws farther.
    private static readonly float s_tpMaxRaw = 2800f;
    private const long ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000L;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int HTCLIENT = 1;
    // ── custom-frame (WindowDesc.CustomFrame) non-client constants ────────────────────────────────────────────────────
    private const uint WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086,
                       WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
                       WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCRBUTTONUP = 0x00A5, WM_NCMOUSELEAVE = 0x02A2,
                       WM_SYSCOMMAND = 0x0112;
    // Non-client pointer stream (mouse-in-pointer): caption interaction arrives as WM_NCPOINTER* — wParam packs the
    // pointer id in the LOW word and the WM_NCHITTEST result in the HIGH word (HTCAPTION/HTMIN/HTMAX/HTCLOSE); lParam is
    // physical SCREEN px (same as the legacy WM_NCLBUTTON* path). WM_NCHITTEST itself is unaffected and stays as-is.
    private const uint WM_NCPOINTERUPDATE = 0x0241, WM_NCPOINTERDOWN = 0x0242, WM_NCPOINTERUP = 0x0243;
    private const uint SIZE_MINIMIZED = 1;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTCLOSE = 20;
    private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030,
                       SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
    private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const uint TME_LEAVE = 0x0002, TME_NONCLIENT = 0x0010;
    private const uint MF_BYCOMMAND = 0x0, MF_ENABLED = 0x0, MF_GRAYED = 0x1;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOSIZE = 0x0001;
    // DIP per wheel notch (120 units) for ELEMENT-level PointerWheel handlers ONLY (e.g. NumberBox). The VIEWPORT scroll
    // distance no longer uses this flat value — it derives from the per-event WheelNotch count as max(48 DIP, 15%·viewport)
    // per notch in the dispatcher (the WinUI content-relative wheel line height, ScrollTuning.PerNotchDip).
    private const float WheelDipPerNotch = 60f;
    // POINTER_INPUT_TYPE (winuser.h): the physical device class behind a WM_POINTER message.
    private const uint PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4, PT_TOUCHPAD = 5;
    // POINTER_BUTTON_CHANGE_TYPE (winuser.h): which button transitioned on this WM_POINTERDOWN/UP — maps a PT_MOUSE
    // contact back onto the engine's 0=left/1=right/2=middle convention (touch/pen down is always the primary action).
    private const uint POINTER_CHANGE_FIRSTBUTTON_DOWN = 1, POINTER_CHANGE_FIRSTBUTTON_UP = 2,
                       POINTER_CHANGE_SECONDBUTTON_DOWN = 3, POINTER_CHANGE_SECONDBUTTON_UP = 4,
                       POINTER_CHANGE_THIRDBUTTON_DOWN = 5, POINTER_CHANGE_THIRDBUTTON_UP = 6;
    // Synthetic PointerId for caption events the NC path injects into the client stream — high enough never to collide
    // with a real OS pointer id (and past InputEventRing.IdSlots so it is simply not coalesced against a real contact).
    private const uint NcSyntheticPointerId = 0xFFFFFFFE;

    // FG_NC_DIAG=1: trace custom-frame caption-button non-client input — which WM_NC* messages actually arrive over a
    // button and whether synthesis runs — to stderr. Off by default (zero cost); diagnoses "no hover / clicks don't
    // fire" with evidence instead of guesses.
    private static readonly bool s_ncDiag =
        System.Environment.GetEnvironmentVariable("FG_NC_DIAG") is "1" or "true";

    private const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOW = 5;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const int GWLP_USERDATA = -21;
    private const int IDC_ARROW = 32512;
    private const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    // OS file/folder drop is handled by Win32DropTarget (a hand-rolled OLE IDropTarget registered via RegisterDragDrop);
    // its shell32/ole32 P/Invokes live there. (The WM_DROPFILES constant below is retained but inert — RegisterDragDrop
    // suppresses WM_DROPFILES.)

    // ── pointer-input interop (winuser.h; not in TerraFX's static-import set, declared locally like the Win32App P/Invokes) ──
    // EnableMouseInPointer is process-wide and irreversible: after it, the mouse no longer raises WM_MOUSE*/WM_MOUSEWHEEL —
    // it raises WM_POINTER* with PT_MOUSE. Returns FALSE if a hook/another component already disabled the conversion.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableMouseInPointer([MarshalAs(UnmanagedType.Bool)] bool fEnable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerType(uint pointerId, uint* pointerType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerInfo(uint pointerId, POINTER_INFO* pointerInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerDevice(nint device, POINTER_DEVICE_INFO* pointerDevice);

    // Drains the OS-coalesced history of ONE pointer's WM_POINTERUPDATE: entriesCount entries, NEWEST first (index 0).
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerInfoHistory(uint pointerId, uint* entriesCount, POINTER_INFO* pointerInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerTouchInfo(uint pointerId, POINTER_TOUCH_INFO* touchInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerPenInfo(uint pointerId, POINTER_PEN_INFO* penInfo);

    // POINTER_INFO (winuser.h) — common to every pointer type. Sequential layout; only the fields the pump reads are
    // named meaningfully, the remainder are padding-faithful placeholders so the OS writes land at the right offsets.
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;            // POINTER_INPUT_TYPE (PT_TOUCH/PT_PEN/PT_MOUSE/PT_TOUCHPAD)
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public nint sourceDevice;           // HANDLE
        public nint hwndTarget;             // HWND
        public POINT ptPixelLocation;       // predicted SCREEN px (motion-corrected for touch)
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;                 // message timestamp (system tick); 0 ⇒ fall back to Now()
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;       // POINTER_BUTTON_CHANGE_TYPE
    }

    private const uint POINTER_DEVICE_TYPE_TOUCH_PAD = 4;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct POINTER_DEVICE_INFO
    {
        public uint displayOrientation;
        public nint device;
        public uint pointerDeviceType;
        public nint monitor;
        public uint startingCursorId;
        public ushort maxActiveContacts;
        public fixed char productString[520];
    }

    // POINTER_TOUCH_INFO (winuser.h) — pressure is 0..1024 (0 when the digitizer reports none).
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;               // 0..1024
    }

    // POINTER_PEN_INFO (winuser.h) — pressure is 0..1024 (0 when the digitizer reports none).
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;               // 0..1024
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    private const string ClassName = "FluentGpuWindow";
    private static ushort s_atom;
    [ThreadStatic] private static Win32Window? s_constructing;

    private readonly HWND _hwnd;
    // Precision-touchpad wheel detection (latched per gesture). A detented mouse wheel always reports the HIWORD delta as
    // an EXACT multiple of WHEEL_DELTA (120); a precision touchpad streams high-resolution sub-120 / non-multiple deltas
    // (MS "highResolutionScrollingAware" docs; Chromium uses the same signature). GetPointerInfo/GetPointerDevice do NOT
    // surface PT_TOUCHPAD for these PT_MOUSE-promoted wheel messages on this hardware, so the delta signature — not the
    // device API — is the reliable classifier. We latch the mode for the whole gesture and only re-evaluate after an idle
    // gap, so a stray 120-multiple mid-stream can't flip the physics path (a true mouse is always 120-multiples).
    private uint _lastWheelMs;
    private bool _wheelHiRes;
    private const uint WheelGestureGapMs = 200;   // gap that ends a wheel gesture and re-evaluates the hi-res latch
    private readonly Dictionary<nint, bool> _touchpadDeviceCache = new();   // source HANDLE -> POINTER_DEVICE_TYPE_TOUCH_PAD
    private readonly GCHandle _self;
    private readonly Queue<InputEvent> _queue = new();
    private Win32TextInput _textInput = null!;   // created right after the HWND exists (WndProc IME cases route to it)
    private UiaProviderCcw* _uiaProvider;        // the window's minimal UIA root provider (the live-region announcer)
    private int _w, _h;
    private float _scale = 1f;
    private bool _closed;
    private DropRegistration? _dropReg;   // OS file/folder drop registration (Win32DropTarget IDropTarget); null = not registered

    // ── custom-frame state (WindowDesc.CustomFrame): engine-reported NC regions + NC-pointer synthesis ──────────────
    private readonly bool _customFrame;
    // WindowDesc.Composited: the visible pixels are a DComp flip surface bound to the HWND (WS_EX_NOREDIRECTIONBITMAP, no
    // redirection bitmap). DWM re-composites that surface at the HWND's new screen position on a pure move, so a composited
    // window needs NO per-step WM_MOVE repaint to track the cursor — the per-step paint is pure overhead/lag there.
    private readonly bool _composited;
    private TitleBarRegion[] _ncRegions = [];
    private int _ncRegionCount;
    private TitleBarHit _ncHover = TitleBarHit.Client;   // Client = "none of our buttons" sentinel in NC context
    private TitleBarHit _ncPress = TitleBarHit.Client;
    private bool _ncInside;       // pointer currently in the non-client area (clears stale engine hover on entry)
    private bool _ncTracking;     // TME_NONCLIENT leave-tracking armed (legacy WM_NCMOUSE* fallback only)
    private bool _ncPointerSeen;  // a WM_NCPOINTER* arrived → the legacy WM_NCMOUSE* fallback stands down (no double-fire)
    private bool _active = true;  // WM_(NC)ACTIVATE — IsActive pull side
    private bool _wasZoomed;      // WM_SIZE edge-detect → InputKind.WindowStateChanged
    private bool _inMoveSizeLoop; // WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE modal loop
    private bool _sizedInMoveSizeLoop; // true once this modal loop has delivered WM_SIZE (edge resize, not pure titlebar move)
    private bool _sizePaintPosted; // a coalesced modal-resize paint is already queued
    private static readonly Point2 OffscreenDip = new(-10000f, -10000f);

    // ── single-instance activation redirect (WM_COPYDATA receiver) ──────────────────────────────────────────────────
    // COPYDATASTRUCT is not in the TerraFX static-import surface, so it is declared locally (the file's convention for
    // ABI shapes TerraFX omits; SetForegroundWindow IS in TerraFX and is used directly, like ShowWindow/SetWindowPos
    // elsewhere here). The cookie MUST equal FluentGpu.WindowsApi.Activation.SingleInstanceGate.ActivationCopyDataCookie
    // ('F''G''A''C') — the two assemblies are independent peers and cannot share the constant.
    private const nuint ActivationCopyDataCookie = 0x46474143;

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public nuint dwData; public uint cbData; public nint lpData; }

    public Win32Window(in WindowDesc desc)
    {
        float requestedW = MathF.Max(1f, desc.SizePx.Width);
        float requestedH = MathF.Max(1f, desc.SizePx.Height);
        _w = (int)requestedW;
        _h = (int)requestedH;
        _customFrame = desc.CustomFrame;   // must be set BEFORE CreateWindowExW: WM_NCCALCSIZE fires during creation
        _composited = desc.Composited;     // gates the WM_MOVE per-step repaint: DWM relocates the DComp surface on a pure move
        _self = GCHandle.Alloc(this);

        HINSTANCE hinst = GetModuleHandleW(null);

        if (s_atom == 0)
        {
            // Process-wide + irreversible: route the mouse through the unified WM_POINTER pipeline (PT_MOUSE) so the one
            // pump path serves mouse, touch and pen. Done once at first window creation, before any window exists.
            EnableMouseInPointer(true);
            fixed (char* cn = ClassName)
            {
                WNDCLASSEXW wc = default;
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.style = CS_HREDRAW | CS_VREDRAW;
                wc.lpfnWndProc = &StaticWndProc;
                wc.hInstance = hinst;
                wc.hCursor = LoadCursorW(default, (char*)IDC_ARROW);
                // Window icon (taskbar / Alt-Tab / NC): load the deployed multi-res app .ico explicitly. This sets the
                // LIVE window's icon (overriding any stale per-exe icon cache) and is id-independent — LoadIcon(hinst,1)
                // fails because the .NET SDK doesn't fix the <ApplicationIcon> at resource id 1. AppContext.BaseDirectory
                // is the exe/asset dir for run / AOT-publish / MSIX-install alike. Null-safe (no-op if the .ico is absent).
                nint appIcon = LoadAppIcon();
                if (appIcon != 0) { wc.hIcon = (HICON)appIcon; wc.hIconSm = (HICON)appIcon; }
                wc.lpszClassName = cn;
                s_atom = RegisterClassExW(&wc);
            }
        }

        RECT rc = new() { left = 0, top = 0, right = _w, bottom = _h };
        AdjustForFrame(&rc);

        s_constructing = this;
        fixed (char* cn = ClassName)
        fixed (char* title = desc.Title)
        {
            _hwnd = CreateWindowExW(
                desc.Composited ? WS_EX_NOREDIRECTIONBITMAP : 0, cn, title, WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, rc.right - rc.left, rc.bottom - rc.top,
                HWND.NULL, HMENU.NULL, hinst, (void*)GCHandle.ToIntPtr(_self));
        }
        s_constructing = null;
        _textInput = new Win32TextInput(_hwnd);

        // Re-run WM_NCCALCSIZE under the custom-frame policy (it already ran during creation with _customFrame set,
        // but a frame-changed pass is the documented way to make the new client geometry stick).
        if (_customFrame)
        {
            SetWindowPos(_hwnd, HWND.NULL, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            _wasZoomed = IsZoomed(_hwnd);
        }

        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1f : dpi / 96f;
        ResizeClientPhysical((int)MathF.Round(requestedW * _scale), (int)MathF.Round(requestedH * _scale));

        RefreshClientSize();

        // Register this top-level window as an OS file/folder drop target via the SAFE hand-rolled OLE IDropTarget
        // (Win32DropTarget) — restoring drag-over HOVER feedback (DragEnter/Over/Leave → the engine external-drop seam)
        // and the OS drop-effect cursor, which WM_DROPFILES cannot. Best-effort: null on a non-STA thread / if it fails
        // (the window then receives no OS drops). Revoked in Dispose. (RegisterDragDrop SUPPRESSES WM_DROPFILES.)
        _dropReg = Win32DropTarget.Register(_hwnd);

        // A minimal UIA root provider for the window (served via WM_GETOBJECT) + the live-region announcer the app reaches
        // through InputHooks.Announce. Best-effort a11y: a null/failed provider just means no screen-reader announcements.
        _uiaProvider = UiaProviderCcw.Create((nint)_hwnd);
        FluentGpu.Hooks.InputHooks.Current.Default.Announce = AnnounceUia;
    }

    /// <summary>Desired-client → window-rect outsets. Standard frame: <c>AdjustWindowRectEx</c>. Custom frame: the
    /// caption is reclaimed as client (WM_NCCALCSIZE keeps the top at 0 inset), so only the L/R/B thin frame remains.</summary>
    private void AdjustForFrame(RECT* rc)
    {
        if (!_customFrame)
        {
            AdjustWindowRectEx(rc, WS_OVERLAPPEDWINDOW, false, 0);
            return;
        }
        uint dpi = _hwnd != HWND.NULL ? GetDpiForWindow(_hwnd) : 96u;
        if (dpi == 0) dpi = 96u;
        int padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        int fx = padded + GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi);
        int fy = padded + GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi);
        rc->left -= fx; rc->right += fx; rc->bottom += fy;   // top unchanged: the client extends to the window top
    }

    private void RefreshClientSize()
    {
        RECT cr;
        GetClientRect(_hwnd, &cr);
        _w = cr.right - cr.left;
        _h = cr.bottom - cr.top;
    }

    public NativeHandle Handle => new(_hwnd, NativeHandleKind.Hwnd);
    public Size2 ClientSizePx => new(_w, _h);
    public float Scale => _scale;
    public bool IsClosed => _closed;
    public bool InModalLoop => _inMoveSizeLoop;   // WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE — host suppresses redundant keep-alive paints
    public Action? PaintRequested { get; set; }

    /// <summary>Screen position of client (0,0) in physical px (<c>ClientToScreen</c>) — the DIP→screen bridge for
    /// popup-window placement and per-monitor work-area queries.</summary>
    public Point2 ClientOriginPx
    {
        get
        {
            POINT pt = default;
            ClientToScreen(_hwnd, &pt);
            return new Point2(pt.x, pt.y);
        }
    }

    // ── popup-window support (Win32PopupWindow forwards its mouse input here, owner-client coords) ──────────────────
    internal HWND Hwnd => _hwnd;
    internal float ScaleInternal => _scale;
    internal void EnqueueExternal(in InputEvent e) => _queue.Enqueue(e);

    /// <summary>Inject a synthetic input event onto the SAME queue the WndProc feeds (parity with HeadlessPlatform.QueueInput):
    /// it is drained by <see cref="PumpInto"/> into the dispatcher ring identically to a real OS message, so a test/diagnostic
    /// harness exercises the full hit-test → routing → inertia path (not the WriteScrollOffset bypass). UI-thread only.</summary>
    public void QueueInput(in InputEvent e) => _queue.Enqueue(e);

    public void Show()
    {
        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);
    }

    /// <summary>Resize the window so the client area is exactly w×h px (drives a real WM_SIZE → resize path). For tests.</summary>
    public void SetClientSize(int w, int h)
        => ResizeClientPhysical(w, h);

    private void ResizeClientPhysical(int w, int h)
    {
        RECT rc = new() { left = 0, top = 0, right = w, bottom = h };
        AdjustForFrame(&rc);
        SetWindowPos(_hwnd, HWND.NULL, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        RefreshClientSize();
    }

    public void SetTitle(StringId title) { }
    public IPlatformTextInput TextInput => _textInput;

    // ── custom-titlebar seam (IPlatformWindow; live only when WindowDesc.CustomFrame) ────────────────────────────────

    /// <summary>Store the engine-reported NC regions (CLIENT DIP — converted to physical px at hit-test time so DPI
    /// hops need no re-report beyond the engine's own relayout push). Reused buffer; copy is per-relayout, not per-frame.</summary>
    public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions)
    {
        if (_ncRegions.Length < regions.Length) _ncRegions = new TitleBarRegion[Math.Max(8, regions.Length)];
        regions.CopyTo(_ncRegions);
        _ncRegionCount = regions.Length;
    }

    public WindowState State
        => IsZoomed(_hwnd) ? WindowState.Maximized : IsIconic(_hwnd) ? WindowState.Minimized : WindowState.Normal;

    public bool IsActive => _active;

    public void Minimize() => PostMessageW(_hwnd, WM_SYSCOMMAND, SC_MINIMIZE, 0);

    public void ToggleMaximize()
        => PostMessageW(_hwnd, WM_SYSCOMMAND, IsZoomed(_hwnd) ? SC_RESTORE : SC_MAXIMIZE, 0);

    public void CloseWindow() => PostMessageW(_hwnd, WM_CLOSE, 0, 0);

    /// <summary>First engine region matching <paramref name="px"/>,<paramref name="py"/> (client PHYSICAL px) → its
    /// HT code; 0 = no region. Report order is the priority order (islands → buttons → caption catch-all).</summary>
    private int HitTestRegions(int px, int py, bool buttonsOnly)
    {
        float s = _scale <= 0f ? 1f : _scale;
        for (int i = 0; i < _ncRegionCount; i++)
        {
            ref readonly TitleBarRegion r = ref _ncRegions[i];
            bool isButton = r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton;
            if (buttonsOnly && !isButton) continue;
            RectF rc = r.RectDip;
            if (px < rc.X * s || px >= (rc.X + rc.W) * s || py < rc.Y * s || py >= (rc.Y + rc.H) * s) continue;
            return r.Hit switch
            {
                TitleBarHit.MinButton => HTMINBUTTON,
                TitleBarHit.MaxButton => HTMAXBUTTON,
                TitleBarHit.CloseButton => HTCLOSE,
                TitleBarHit.Caption => HTCAPTION,
                _ => HTCLIENT,
            };
        }
        return 0;
    }

    private static TitleBarHit NcHitFromCode(long ht) => ht switch
    {
        HTMINBUTTON => TitleBarHit.MinButton,
        HTMAXBUTTON => TitleBarHit.MaxButton,
        HTCLOSE => TitleBarHit.CloseButton,
        _ => TitleBarHit.Client,   // sentinel: not one of the engine-drawn buttons
    };

    /// <summary>Resolve the engine caption-button hit for an NC pointer/mouse message from its SCREEN position
    /// (lParam) using the SAME region test WM_NCHITTEST uses. The packed hit-test code carried in the message's
    /// wParam is unreliable here (observed under mouse-in-pointer: the WM_NCPOINTER* wParam high word decodes to
    /// Client even when WM_NCHITTEST returns HTMIN/HTMAX/HTCLOSE for the identical pixel) — so we re-derive from
    /// the position, which the live HITTEST trace proves correct.</summary>
    private TitleBarHit NcHitAtScreen(HWND hWnd, long lp)
    {
        POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
        ScreenToClient(hWnd, &pt);
        if (IsZoomed(hWnd) && pt.y < 0) pt.y = 0;   // maximized: the caption row sits at negative client y — fold to row 0
        return NcHitFromCode(HitTestRegions(pt.x, pt.y, buttonsOnly: true));
    }

    /// <summary>Center of the reported region for <paramref name="hit"/>, in DIP — the synthetic-pointer target that
    /// drives the engine button's hover/press exactly like a real pointer.</summary>
    private Point2 NcCenterDip(TitleBarHit hit)
    {
        for (int i = 0; i < _ncRegionCount; i++)
        {
            if (_ncRegions[i].Hit != hit) continue;
            RectF r = _ncRegions[i].RectDip;
            return new Point2(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);
        }
        return OffscreenDip;
    }

    // ── shared NC → engine synthesis (WM_NCPOINTER* and the legacy WM_NCMOUSE*/WM_NCLBUTTON* fallback feed identical
    //    events). Synthesized client events carry NcSyntheticPointerId so they never collide/coalesce with real contacts.

    /// <summary>NC hover over <paramref name="ncHit"/> → engine PointerMove at the button center (or offscreen off the
    /// buttons). Returns true (consumed, no classic NC UI) while over an engine button, false to fall through otherwise.</summary>
    private bool NcHover(HWND hWnd, TitleBarHit ncHit, out LRESULT result)
    {
        result = default;
        if (s_ncDiag) System.Console.Error.WriteLine($"[NC] hover-msg hit={ncHit} ncPointerSeen={_ncPointerSeen} inside={_ncInside}");
        bool changed = !_ncInside;   // client → NC crossing: pull engine hover off whatever was hovered
        _ncInside = true;
        if (ncHit != _ncHover) { _ncHover = ncHit; changed = true; }
        if (changed)
        {
            _queue.Enqueue(new InputEvent(InputKind.PointerMove,
                ncHit == TitleBarHit.Client ? OffscreenDip : NcCenterDip(ncHit), 0, 0,
                Mods: Mods(), TimestampMs: Now(), PointerId: NcSyntheticPointerId));
            PaintRequested?.Invoke();   // NC interactions can arrive while the frame loop idles
        }
        if (ncHit != TitleBarHit.Client) { result = 0; return true; }   // eat over engine buttons (no classic NC UI)
        return false;
    }

    /// <summary>NC press on <paramref name="ncHit"/> → engine PointerDown at the button center. HTCAPTION/resize falls
    /// through to DefWindowProc (OS drag-move/size loop, double-click maximize).</summary>
    private bool NcPress(TitleBarHit ncHit, out LRESULT result)
    {
        result = default;
        if (s_ncDiag) System.Console.Error.WriteLine($"[NC] press-msg hit={ncHit}");
        if (ncHit == TitleBarHit.Client) return false;
        _ncPress = ncHit;
        _queue.Enqueue(new InputEvent(InputKind.PointerDown, NcCenterDip(ncHit), 0, 0,
            Mods: Mods(), TimestampMs: Now(), PointerId: NcSyntheticPointerId));
        PaintRequested?.Invoke();
        result = 0;
        return true;   // consumed: DefWindowProc would draw the classic NC button press
    }

    /// <summary>NC release → click (engine OnClick invokes Minimize/ToggleMaximize/Close) when over the pressed button,
    /// else cancel (offscreen up clears the pressed state without firing). Falls through if no NC press was held.</summary>
    private bool NcRelease(TitleBarHit ncHit, out LRESULT result)
    {
        result = default;
        if (s_ncDiag) System.Console.Error.WriteLine($"[NC] release-msg hit={ncHit} press={_ncPress}");
        if (_ncPress == TitleBarHit.Client) return false;
        _queue.Enqueue(new InputEvent(InputKind.PointerUp,
            ncHit == _ncPress ? NcCenterDip(_ncPress) : OffscreenDip, 0, 0,
            Mods: Mods(), TimestampMs: Now(), PointerId: NcSyntheticPointerId));
        _ncPress = TitleBarHit.Client;
        PaintRequested?.Invoke();
        result = 0;
        return true;
    }

    private CursorId _cursor = CursorId.Arrow;

    /// <summary>Set the client-area cursor (applied immediately and re-asserted on every WM_SETCURSOR over the client).</summary>
    public void SetCursor(CursorId id)
    {
        _cursor = id;
        ApplyCursor();
    }

    private void ApplyCursor()
        => TerraFX.Interop.Windows.Windows.SetCursor(LoadCursorW(default, (char*)IdcFor(_cursor)));

    private static int IdcFor(CursorId id) => id.Value switch
    {
        1 => 32513,   // IDC_IBEAM
        2 => 32649,   // IDC_HAND
        3 => 32644,   // IDC_SIZEWE
        4 => 32645,   // IDC_SIZENS
        5 => 32642,   // IDC_SIZENWSE
        6 => 32643,   // IDC_SIZENESW
        7 => 32646,   // IDC_SIZEALL
        8 => 32515,   // IDC_CROSS
        9 => 32648,   // IDC_NO
        10 => 32514,  // IDC_WAIT
        _ => IDC_ARROW,
    };

    public int PumpInto(InputEventRing ring)
    {
        MSG msg;
        while (PeekMessageW(&msg, HWND.NULL, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        int n = 0;
        while (_queue.Count > 0) { ring.Write(_queue.Dequeue()); n++; }
        return n;
    }

    public void WaitForWork(int timeoutMs)
    {
        uint timeout = timeoutMs < 0 ? 0xFFFFFFFF : (uint)timeoutMs;
        MsgWaitForMultipleObjectsEx(0, null, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }

    /// <summary>Thread-safe wake (IPlatformWindow.Wake): post a benign WM_NULL so a blocked <see cref="WaitForWork"/>
    /// returns and the loop drains a cross-thread UI post. PostMessageW is documented thread-safe, so a worker/COM thread
    /// may call this directly; WM_NULL (0) is discarded by the pump after waking.</summary>
    public void Wake() => PostMessageW(_hwnd, 0u /* WM_NULL */, 0, 0);

    /// <summary>Raise a screen-reader announcement (UIA live region) on this window's provider — wired onto
    /// <see cref="FluentGpu.Hooks.InputHooks"/>.Announce. Best-effort; a no-op when no assistive tech is listening.</summary>
    internal void AnnounceUia(string text, bool assertive) => Win32Uia.Announce(_uiaProvider, text, assertive);

    public void Dispose()
    {
        Win32DropTarget.Revoke(_dropReg);   // RevokeDragDrop + free the CCW before the HWND dies
        _dropReg = null;
        _textInput?.DisposeSip();   // release the WinRT InputPane refs + SIP event subscriptions before the HWND dies
        if (_hwnd != HWND.NULL) { KillTimer(_hwnd, MoveLoopTimerId); DestroyWindow(_hwnd); }
        // The UIA provider CCW is intentionally LEAKED (not freed): UIA may still hold a ref after the HWND dies and a
        // synchronous free would risk a use-after-free. A few bytes, one per window, reclaimed at process exit.
        if (_uiaProvider != null) { _uiaProvider = null; FluentGpu.Hooks.InputHooks.Current.Default.Announce = null; }
        if (_self.IsAllocated) _self.Free();
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW_File(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    /// <summary>Load the app's deployed multi-res icon (<c>assets/AppIcon/appicon.ico</c>, beside the exe) as an HICON
    /// for the window class. IMAGE_ICON=1, LR_LOADFROMFILE=0x10 | LR_DEFAULTSIZE=0x40. Returns 0 if the file is absent.</summary>
    private static nint LoadAppIcon()
    {
        string ico = Path.Combine(AppContext.BaseDirectory, "assets", "AppIcon", "appicon.ico");
        return File.Exists(ico) ? LoadImageW_File(0, ico, 1, 0, 0, 0x10 | 0x40) : 0;
    }

    [UnmanagedCallersOnly]
    private static LRESULT StaticWndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_NCCREATE)
        {
            CREATESTRUCTW* cs = (CREATESTRUCTW*)(nint)lParam;
            SetWindowLongPtrW(hWnd, GWLP_USERDATA, (nint)cs->lpCreateParams);
        }

        nint ud = GetWindowLongPtrW(hWnd, GWLP_USERDATA);
        Win32Window? self = ud != 0 ? GCHandle.FromIntPtr(ud).Target as Win32Window : s_constructing;
        if (self is not null && self.Handle32(hWnd, msg, wParam, lParam, out LRESULT r)) return r;
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool Handle32(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = default;
        long lp = (long)(nint)lParam;
        switch (msg)
        {
            case WM_DESTROY: _closed = true; PostQuitMessage(0); return true;
            case WM_CLOSE: _closed = true; DestroyWindow(hWnd); return true;
            case Win32Uia.WM_GETOBJECT:
                // Hand the UIA root provider to a connecting client; non-UIA object ids fall through to DefWindowProc.
                if (Win32Uia.HandleGetObject((nint)hWnd, (nint)wParam, (nint)lParam, _uiaProvider, out nint uiaRes)) { result = (LRESULT)uiaRes; return true; }
                return false;
            case WM_ERASEBKGND: result = (LRESULT)1; return true;   // we paint every pixel — suppress the flicker-erase
            case WM_SIZE:
                // Iconic size is 0×0 — adopting it would churn a degenerate swapchain resize + full relayout, and the
                // zoom edge-detect below would mis-fire on the maximized→minimize edge (IsZoomed false while iconic).
                if ((nuint)wParam == SIZE_MINIMIZED) return true;
                if (_inMoveSizeLoop) _sizedInMoveSizeLoop = true;
                _w = (int)(lp & 0xFFFF); _h = (int)((lp >> 16) & 0xFFFF);
                if (_customFrame)
                {
                    // Maximize/restore edge → the custom titlebar re-glyphs max↔restore (and re-reports regions
                    // through the relayout the size change triggers).
                    bool zoomedNow = IsZoomed(hWnd);
                    if (zoomedNow != _wasZoomed)
                    {
                        _wasZoomed = zoomedNow;
                        _queue.Enqueue(new InputEvent(InputKind.WindowStateChanged, default, 0, 0, TimestampMs: Now()));
                    }
                }
                if (_composited && _inMoveSizeLoop)
                {
                    if (!_sizePaintPosted)
                    {
                        _sizePaintPosted = true;
                        PostMessageW(hWnd, WM_FG_COALESCED_SIZE_PAINT, 0, 0);
                        if (Diag.EnvFlag("FG_MOVE_DIAG")) Console.Error.WriteLine("[FG_MOVE_DIAG] posted coalesced resize paint");
                    }
                    return true;
                }
                PaintRequested?.Invoke();   // keep the window live during the modal resize loop
                return true;
            case WM_FG_COALESCED_SIZE_PAINT:
                if (!_sizePaintPosted) return true;   // stale queued paint; WM_EXITSIZEMOVE already painted the settle frame
                _sizePaintPosted = false;
                if (Diag.EnvFlag("FG_MOVE_DIAG")) Console.Error.WriteLine("[FG_MOVE_DIAG] coalesced resize paint");
                PaintRequested?.Invoke();
                return true;
            case 0x02E0:   // WM_DPICHANGED (WinUser.h — not surfaced by the TerraFX static-import set)
            {
                // Per-Monitor V2: the OS does NOT bitmap-stretch — it hands us the new DPI + a suggested window rect
                // on the target monitor and expects us to adopt both. Update the scale FIRST (the SetWindowPos below
                // raises WM_SIZE → the host's resize path re-lays-out in DIPs / re-rasterizes glyphs at the new
                // scale), then take the suggested rect so the window keeps its apparent (DIP) size across monitors.
                _scale = ((uint)wParam & 0xFFFF) / 96f;   // LOWORD = X DPI (X and Y are always equal)
                RECT* suggested = (RECT*)(nint)lParam;
                SetWindowPos(_hwnd, HWND.NULL, suggested->left, suggested->top,
                    suggested->right - suggested->left, suggested->bottom - suggested->top,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                RefreshClientSize();
                PaintRequested?.Invoke();
                return true;
            }
            case WM_PAINT:
                PaintRequested?.Invoke();
                ValidateRect(hWnd, null);
                return true;
            case WM_SETTINGCHANGE:
                // OS Settings change. When the changed area is the immersive color set (Settings ▸ Colors: app dark/light
                // flip or accent change) notify the host so it can re-read OS state and re-theme live. lParam is an
                // LPCWSTR naming the area (may be null for some broadcasts). Don't consume it — let DefWindowProc run too.
                if ((nint)lParam != 0 && Marshal.PtrToStringUni((nint)lParam) == "ImmersiveColorSet")
                    Win32App.RaiseSystemColorsChanged();
                return false;
            case WM_ENTERSIZEMOVE:
                // Entered the OS modal move/size loop. Arm a ~120 Hz timer so frames keep flowing (animations, caret,
                // scroll settle) even when the user holds the window still mid-drag — WM_MOVE alone fires only on motion.
                _inMoveSizeLoop = true;
                _sizedInMoveSizeLoop = false;
                if (Diag.EnvFlag("FG_MOVE_DIAG"))
                    Console.Error.WriteLine("[FG_MOVE_DIAG] enter modal move/size");
                SetTimer(hWnd, MoveLoopTimerId, 8, null);
                return true;
            case WM_EXITSIZEMOVE:
                KillTimer(hWnd, MoveLoopTimerId);
                if (Diag.EnvFlag("FG_MOVE_DIAG"))
                    Console.Error.WriteLine($"[FG_MOVE_DIAG] exit modal move/size sized={_sizedInMoveSizeLoop}");
                _inMoveSizeLoop = false;
                _sizedInMoveSizeLoop = false;
                _sizePaintPosted = false;
                PaintRequested?.Invoke();   // one settle frame at the final position
                return true;
            case WM_TIMER:
                if ((nuint)wParam == MoveLoopTimerId)
                {
                    // During a pure titlebar MOVE of a composited window, DWM already moves the bound DComp surface with
                    // the HWND. Repainting on this low-priority timer because animations/playback are active only burns
                    // WndProc time and makes the OS modal loop feel sticky. Keep timer paints for non-composited windows
                    // and for resize loops; WM_SIZE is coalesced through WM_FG_COALESCED_SIZE_PAINT during edge-resize.
                    if (_composited && _inMoveSizeLoop && !_sizedInMoveSizeLoop)
                    {
                        if (Diag.EnvFlag("FG_MOVE_DIAG")) Console.Error.WriteLine($"[FG_MOVE_DIAG t={Environment.TickCount64}] timer skipped: pure composited move");
                        return true;
                    }
                    if (Diag.EnvFlag("FG_MOVE_DIAG")) Console.Error.WriteLine($"[FG_MOVE_DIAG t={Environment.TickCount64}] timer paint");
                    PaintRequested?.Invoke();
                    return true;
                }
                return false;
            case WM_MOVE:
                // Composited window: the visible pixels are a DComp flip surface bound to the HWND, which DWM re-composites
                // at the new screen position on a pure move — no app repaint is needed for the content to track the window,
                // so the per-step paint is dropped (it's pure record+submit+present overhead that lags the modal loop). The
                // WM_ENTERSIZEMOVE 8 ms timer keeps animations/caret live mid-drag and WM_EXITSIZEMOVE paints one settle
                // frame at the final position — both unchanged. Non-composited / redirection-bitmap windows still repaint
                // per step so their content doesn't trail the cursor.
                if (Diag.EnvFlag("FG_MOVE_DIAG")) Console.Error.WriteLine($"[FG_MOVE_DIAG t={Environment.TickCount64}] WM_MOVE composited={_composited}");
                if (!_composited) PaintRequested?.Invoke();
                return true;
            // ── pointer input (mouse-in-pointer: PT_MOUSE/PT_TOUCH/PT_PEN all arrive here) ──────────────────────────────
            // The client moved means we are no longer in the NC area: if a caption press was held and dragged into the
            // client (the NC path holds no capture), cancel it exactly like the NC-leave path, then let real pointer
            // updates own hover again.
            case WM_POINTERUPDATE:
                if (_ncPress != TitleBarHit.Client)
                {
                    _ncPress = TitleBarHit.Client;
                    // The NC press was synthesized on NcSyntheticPointerId — its cancel MUST carry the same reserved id so
                    // it releases ONLY the synthetic NC slot and can never cancel a real contact (a real mouse is id 0).
                    _queue.Enqueue(new InputEvent(InputKind.PointerUp, OffscreenDip, 0, 0,
                        TimestampMs: Now(), PointerId: NcSyntheticPointerId));
                }
                _ncInside = false; _ncHover = TitleBarHit.Client;
                PointerUpdate(GET_POINTERID_WPARAM(wParam));
                return true;
            case WM_POINTERDOWN:
                PointerDownUp(GET_POINTERID_WPARAM(wParam), down: true);
                return true;
            case WM_POINTERUP:
                PointerDownUp(GET_POINTERID_WPARAM(wParam), down: false);
                return true;
            case WM_POINTERLEAVE:
            {
                // The hovering pointer left the window (the WM_POINTER analogue of WM_MOUSELEAVE): clear engine hover so
                // it never sticks. Mouse parks the cursor offscreen; a touch/pen contact that lifted already sent its up.
                uint leaveId = GET_POINTERID_WPARAM(wParam);
                _queue.Enqueue(new InputEvent(InputKind.PointerMove, OffscreenDip, 0, 0,
                    Pointer: PointerKindOf(leaveId), TimestampMs: Now(), PointerId: leaveId));
                return true;
            }
            case WM_POINTERCAPTURECHANGED:
            {
                // Per-pointer capture loss (gesture stolen, contact aborted): cancel just THAT contact's interaction —
                // the dispatcher releases that PointerId's capture/press without firing a click.
                uint capId = GET_POINTERID_WPARAM(wParam);
                _queue.Enqueue(new InputEvent(InputKind.PointerCancel, default, 0, 0,
                    Pointer: PointerKindOf(capId), TimestampMs: Now(), PointerId: capId));
                return true;
            }
            case WM_POINTERWHEEL:
            case WM_POINTERHWHEEL:
            {
                // HIWORD(wParam) = signed notch delta (×120). WM_POINTERWHEEL = VERTICAL, WM_POINTERHWHEEL = horizontal.
                short notch = unchecked((short)((ulong)(nuint)wParam >> 16));
                bool horizontal = msg == WM_POINTERHWHEEL;
                // Classify touchpad (hi-res, non-120-multiple deltas) vs detented mouse (120-multiples), latched per gesture
                // (idle gap > WheelGestureGapMs re-evaluates). PointerKindOf corroborates when a stack exposes the device.
                uint nowMs = Now();
                bool streamIdle = nowMs - _lastWheelMs > WheelGestureGapMs;
                _lastWheelMs = nowMs;
                bool thisHiRes = notch != 0 && (notch % 120) != 0;
                if (streamIdle) _wheelHiRes = thisHiRes;
                else if (thisHiRes) _wheelHiRes = true;
                uint wheelPid = GET_POINTERID_WPARAM(wParam);
                if (PointerKindOf(wheelPid) == PointerKind.Touchpad) _wheelHiRes = true;

                // Precision-touchpad pan (hi-res): the engine owns it. The OS-promoted hi-res WM_POINTERWHEEL packet is
                // soft-kneed and scaled here, then handed to the engine's packet-driven PanTouchpad path.
                if (_wheelHiRes)
                {
                    // Soft-knee on the raw notch BEFORE scaling: |notch| ≤ s_tpKnee stays exactly linear (precise panning
                    // untouched); above the knee the surplus is compressed so the curve asymptotes toward s_tpMaxRaw (a big
                    // accelerated packet no longer blasts through the whole list). s_tpScale is applied AFTER (unchanged).
                    float a = MathF.Abs((float)notch);
                    float tamed = a <= s_tpKnee
                        ? a
                        : s_tpKnee + (s_tpMaxRaw - s_tpKnee) * (1f - MathF.Exp(-(a - s_tpKnee) / (s_tpMaxRaw - s_tpKnee)));
                    float dip = MathF.Sign((float)notch) * tamed * s_tpScale;
                    // Vertical: −delta = scroll toward content end (offset increases). Horizontal: +delta = right (offset increases).
                    float tpDipY = horizontal ? 0f : -dip;
                    float tpDipX = horizontal ? dip : 0f;
                    if (FluentGpu.Foundation.ScrollLog.On)
                    {
                        // DIAGNOSTIC: dump the pointer flags so a trace shows whether Windows distinguishes an active
                        // two-finger scroll (POINTER_FLAG_INCONTACT 0x04 set) from the post-lift momentum tail (INCONTACT
                        // cleared) — the clean "finger lifted" signal we want instead of guessing from the packet trend.
                        uint flags = 0;
                        POINTER_INFO wpi;
                        if (GetPointerInfo(wheelPid, &wpi)) flags = wpi.pointerFlags;
                        FluentGpu.Foundation.ScrollLog.Line(
                            $"TPPAN   {(horizontal ? "H" : "V")} notch={notch} dip={(horizontal ? tpDipX : tpDipY):0.0} " +
                            $"flags=0x{flags:X5} inContact={((flags & 0x4u) != 0)} up={((flags & 0x40000u) != 0)} new={((flags & 0x1u) != 0)}");
                    }
                    _queue.Enqueue(new InputEvent(InputKind.Wheel, WheelPt(lp), 0, 0, tpDipY, Mods(),
                        Pointer: PointerKind.Touchpad, TimestampMs: nowMs,
                        PointerId: wheelPid, ScrollDeltaX: tpDipX, WheelNotch: 0f, WheelNotchX: 0f));
                    return true;
                }

                // Vertical: +notch = scroll up, flipped to "positive = toward content end". Horizontal: +notch = right (no
                // flip). The DIP (via WheelDipPerNotch) is retained ONLY for ELEMENT-level PointerWheel handlers (NumberBox).
                float notchY = horizontal ? 0f : -(notch / 120f);
                float notchX = horizontal ? (notch / 120f) : 0f;
                float dipY = notchY * WheelDipPerNotch;
                float dipX = notchX * WheelDipPerNotch;
                PointerKind wheelKind = _wheelHiRes ? PointerKind.Touchpad : PointerKind.Mouse;
                if (FluentGpu.Foundation.ScrollLog.On)
                    FluentGpu.Foundation.ScrollLog.Line($"WHEEL {(horizontal ? "H" : "V")} notch={notch} dip={(horizontal ? dipX : dipY):0.0} hiRes={_wheelHiRes} kind={wheelKind} (notch fallback)");
                _queue.Enqueue(new InputEvent(InputKind.Wheel, WheelPt(lp), 0, 0, dipY, Mods(),
                    Pointer: wheelKind, TimestampMs: nowMs,
                    PointerId: wheelPid, ScrollDeltaX: dipX, WheelNotch: notchY, WheelNotchX: notchX));
                return true;
            }
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                // lParam bit 30 = the key was already down (keyboard auto-repeat).
                _queue.Enqueue(new InputEvent(InputKind.Key, default, 0, (int)(nuint)wParam,
                    Mods: Mods(), IsRepeat: (lp & (1L << 30)) != 0, TimestampMs: Now()));
                // Swallow Alt-chord menu activation (we route access keys ourselves) — but keep Alt+F4 = system close.
                return msg != WM_SYSKEYDOWN || (int)(nuint)wParam != 115 /* VK_F4 */;
            case WM_KEYUP:
            case WM_SYSKEYUP:
                _queue.Enqueue(new InputEvent(InputKind.KeyUp, default, 0, (int)(nuint)wParam, Mods: Mods(), TimestampMs: Now()));
                return true;
            case WM_CHAR:
                // TranslateMessage (run in the pump) synthesizes WM_CHAR from WM_KEYDOWN → the layout/IME-resolved
                // codepoint, carried in the InputEvent.KeyCode slot. Editing/navigation keys still arrive via WM_KEYDOWN.
                _queue.Enqueue(new InputEvent(InputKind.Char, default, 0, (int)(nuint)wParam, Mods: Mods(), TimestampMs: Now()));
                return true;
            case WM_ACTIVATE:
                _active = ((nuint)wParam & 0xFFFF) != 0;
                _queue.Enqueue(new InputEvent(_active ? InputKind.WindowFocus : InputKind.WindowBlur, default, 0, 0, TimestampMs: Now()));
                return true;
            case WM_COPYDATA:
            {
                // Single-instance activation redirect: a second launch forwarded its activation URI to us via
                // SendMessageTimeoutW (FluentGpu.WindowsApi.Activation.SingleInstanceGate). dwData must equal the agreed
                // cookie (kept in sync with SingleInstanceGate.ActivationCopyDataCookie — the two assemblies are
                // independent peers and can't share the constant). Delivered on the UI thread (the OS dispatches a sent
                // message on the receiver's thread), so RaiseActivationRedirected → AppHost.WakeFrame is UI-thread-safe.
                var cds = (COPYDATASTRUCT*)(nint)lParam;
                if (cds is not null && cds->dwData == ActivationCopyDataCookie)
                {
                    string uri = cds->cbData >= sizeof(char)
                        ? new string((char*)cds->lpData, 0, (int)(cds->cbData / sizeof(char)))
                        : string.Empty;
                    SetForegroundWindow(hWnd);   // bring our window up (the sender granted us foreground via ASFW_ANY)
                    Win32App.RaiseActivationRedirected(uri);
                    result = (LRESULT)1;   // TRUE = handled (WM_COPYDATA convention)
                    return true;
                }
                return false;
            }
            case WM_SETCURSOR:
                // Re-assert the engine-chosen cursor while over the client area; let DefWindowProc style the chrome.
                if (((long)(nint)lParam & 0xFFFF) == HTCLIENT) { ApplyCursor(); result = (LRESULT)1; return true; }
                return false;
            case WM_CAPTURECHANGED:
                // Whole-window legacy capture loss. With mouse-in-pointer the engine never calls SetCapture (contacts are
                // implicitly captured per-pointer and their loss surfaces as WM_POINTERCAPTURECHANGED above, which already
                // emits a per-PointerId cancel). Letting this fire a second broad cancel would double-count the release, so
                // it is consumed without re-cancelling — the per-pointer path is the single source of cancellation.
                return true;

            // ── custom frame (WindowDesc.CustomFrame): strip the caption, keep the resize frame (Terminal recipe) ────
            case WM_NCCALCSIZE when _customFrame && (nuint)wParam != 0:
            {
                // DefWindowProc insets all four edges by the standard frame (caption included on top); restoring the
                // original top reclaims the caption strip as client while keeping the thin L/R/B frame — DWM shadow,
                // Win11 rounded corners and the L/R/B resize borders all stay system-handled.
                NCCALCSIZE_PARAMS* p = (NCCALCSIZE_PARAMS*)(nint)lParam;
                int savedTop = p->rgrc[0].top;
                DefWindowProcW(hWnd, msg, wParam, lParam);
                p->rgrc[0].top = savedTop;
                if (IsZoomed(hWnd))
                {
                    // Maximized windows hang their frame off-screen on every edge; DefWindowProc already pulled L/R/B
                    // back in — the reclaimed top must be inset too or the bar renders above the screen edge.
                    uint dpi = GetDpiForWindow(hWnd);
                    p->rgrc[0].top += GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi) + GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi);
                }
                result = 0;
                return true;
            }

            case WM_NCHITTEST when _customFrame:
            {
                // lParam = SCREEN px → client px. Priority: engine caption buttons (they win even inside the top
                // resize band — the Win11 Fitts corner: close stays clickable at y=0), then the top resize band
                // (restored only; L/R/B borders remain DefWindowProc's via the kept thin frame), then the engine
                // regions (islands → HTCLIENT, drag band → HTCAPTION), else DefWindowProc.
                POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
                ScreenToClient(hWnd, &pt);
                bool zoomed = IsZoomed(hWnd);
                // Maximized, the NCCALCSIZE top inset puts the physical screen-top row at NEGATIVE client y — fold it
                // onto the first hittable row so the Fitts slam-zone still reaches close/max/min and the drag band.
                if (zoomed && pt.y < 0) pt.y = 0;
                int button = HitTestRegions(pt.x, pt.y, buttonsOnly: true);
                if (s_ncDiag && button != 0)
                    System.Console.Error.WriteLine($"[NC] HITTEST button={button} pt=({pt.x},{pt.y}) regions={_ncRegionCount} zoomed={zoomed}");
                if (button != 0) { result = (LRESULT)button; return true; }
                if (!zoomed && pt.y >= 0)
                {
                    uint dpi = GetDpiForWindow(hWnd);
                    int rb = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi) + GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi);
                    if (pt.y < rb)
                    {
                        result = (LRESULT)(pt.x < rb ? HTTOPLEFT : pt.x >= _w - rb ? HTTOPRIGHT : HTTOP);
                        return true;
                    }
                }
                int hit = HitTestRegions(pt.x, pt.y, buttonsOnly: false);
                if (hit != 0) { result = (LRESULT)hit; return true; }
                return false;
            }

            // NC pointer → synthesized engine input: HTMIN/HTMAX/HTCLOSE pixels are non-client, so client WM_POINTER*
            // never arrives there. Translating the NC stream into pointer events at the button's center drives the
            // engine-drawn buttons' InteractionAnimator ramps (hover/press/click) exactly like a real pointer. Under
            // mouse-in-pointer the caption mouse arrives here (WM_NCPOINTER*); the legacy WM_NCMOUSE*/WM_NCLBUTTON* cases
            // below stay as a fallback for flows that still emit them, suppressed once a pointer message has been seen.
            // wParam: LOW word = pointer id (unused — caption synthesis uses one reserved id), HIGH word = the
            // WM_NCHITTEST result; lParam = SCREEN px (the OS already drag/size-loops via DefWindowProc for HTCAPTION).
            case WM_NCPOINTERUPDATE when _customFrame:
                _ncPointerSeen = true;
                if (!_ncTracking)
                {
                    // Arm NC leave-tracking so WM_NCMOUSELEAVE still tears the NC hover/press down when the contact leaves
                    // the caption straight off-window (the pointer stream has no NC-specific leave of its own).
                    TRACKMOUSEEVENT tme = new() { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hWnd };
                    TrackMouseEvent(&tme);
                    _ncTracking = true;
                }
                if (s_ncDiag) System.Console.Error.WriteLine($"[NC] NCPOINTERUPDATE wParam=0x{(ulong)(nuint)wParam:X} hi-decode={NcHitFromCode((long)((nuint)wParam >> 16))} derived={NcHitAtScreen(hWnd, lp)}");
                return NcHover(hWnd, NcHitAtScreen(hWnd, lp), out result);

            case WM_NCPOINTERDOWN when _customFrame:
                _ncPointerSeen = true;
                return NcPress(NcHitAtScreen(hWnd, lp), out result);

            case WM_NCPOINTERUP when _customFrame:
                _ncPointerSeen = true;
                return NcRelease(NcHitAtScreen(hWnd, lp), out result);

            case WM_NCMOUSEMOVE when _customFrame:
            {
                if (_ncPointerSeen) { result = 0; return false; }   // the WM_NCPOINTER* path owns NC synthesis
                if (!_ncTracking)
                {
                    TRACKMOUSEEVENT tme = new() { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hWnd };
                    TrackMouseEvent(&tme);
                    _ncTracking = true;
                }
                return NcHover(hWnd, NcHitAtScreen(hWnd, lp), out result);
            }

            case WM_NCMOUSELEAVE when _customFrame:
                _ncTracking = false;
                if (_ncInside)
                {
                    _ncInside = false; _ncHover = TitleBarHit.Client;
                    if (_ncPress != TitleBarHit.Client)
                    {
                        // NC presses have no capture: leaving the NC area cancels the press (offscreen up, no click).
                        _ncPress = TitleBarHit.Client;
                        _queue.Enqueue(new InputEvent(InputKind.PointerUp, OffscreenDip, 0, 0, TimestampMs: Now()));
                    }
                    _queue.Enqueue(new InputEvent(InputKind.PointerMove, OffscreenDip, 0, 0, TimestampMs: Now()));
                    PaintRequested?.Invoke();
                }
                return false;   // let DefWindowProc finish its own tracking teardown

            // DBLCLK rides the same path: NC double-clicks ALWAYS produce WM_NCLBUTTONDBLCLK (CS_DBLCLKS only affects
            // client messages) — unhandled it reaches DefWindowProc with HTMAX/HTMIN/HTCLOSE and posts a SECOND
            // SYSCOMMAND on top of the engine click. Treated as a press, a double-click decomposes into two clean
            // engine clicks; HTCAPTION still falls through so double-click-to-maximize on the drag band keeps working.
            case WM_NCLBUTTONDOWN when _customFrame:
            case WM_NCLBUTTONDBLCLK when _customFrame:
                if (_ncPointerSeen) { result = 0; return false; }
                return NcPress(NcHitAtScreen(hWnd, lp), out result);

            case WM_NCLBUTTONUP when _customFrame:
                if (_ncPointerSeen) { result = 0; return false; }
                return NcRelease(NcHitAtScreen(hWnd, lp), out result);

            case WM_NCRBUTTONUP when _customFrame && (long)(nuint)wParam == HTCAPTION:
            {
                // The shell titlebar context menu (right-click the drag band) with shell-correct enable states;
                // Alt+Space keeps working through DefWindowProc (SC_KEYMENU is not intercepted).
                HMENU sys = GetSystemMenu(hWnd, false);
                if (sys != HMENU.NULL)
                {
                    bool zoomed = IsZoomed(hWnd);
                    EnableMenuItem(sys, SC_RESTORE, MF_BYCOMMAND | (zoomed ? MF_ENABLED : MF_GRAYED));
                    EnableMenuItem(sys, SC_MOVE, MF_BYCOMMAND | (zoomed ? MF_GRAYED : MF_ENABLED));
                    EnableMenuItem(sys, SC_SIZE, MF_BYCOMMAND | (zoomed ? MF_GRAYED : MF_ENABLED));
                    EnableMenuItem(sys, SC_MAXIMIZE, MF_BYCOMMAND | (zoomed ? MF_GRAYED : MF_ENABLED));
                    EnableMenuItem(sys, SC_MINIMIZE, MF_BYCOMMAND | MF_ENABLED);
                    // No SetMenuDefaultItem: the Win11 shell's RIGHT-CLICK caption menu has no default (bold) item —
                    // only the icon double-click path defaults to Close.
                    int cmd = (int)TrackPopupMenu(sys, TPM_RETURNCMD | TPM_RIGHTBUTTON, (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF), 0, hWnd, null);
                    if (cmd != 0) PostMessageW(hWnd, WM_SYSCOMMAND, (WPARAM)(nuint)cmd, 0);
                }
                result = 0;
                return true;
            }

            case WM_NCACTIVATE when _customFrame:
                // Track activation for the IsActive pull (WM_ACTIVATE drives the engine focus/blur events); lParam -1
                // stops DefWindowProc repainting the classic NC title it thinks it owns.
                _active = (nuint)wParam != 0;
                result = DefWindowProcW(hWnd, msg, wParam, (LPARAM)(-1));
                return true;
            // IME composition: we render the composition inline, so the system composition window is suppressed
            // (WM_IME_SETCONTEXT strips ISC_SHOWUICOMPOSITIONWINDOW; WM_IME_STARTCOMPOSITION is consumed) and the
            // composition events flow to the sink directly (strings can't ride the POD ring).
            case WM_IME_SETCONTEXT:
                result = DefWindowProcW(hWnd, msg, wParam, (LPARAM)(nint)(lp & ~ISC_SHOWUICOMPOSITIONWINDOW));
                return true;
            case WM_IME_STARTCOMPOSITION:
                _textInput?.OnStartComposition();   // null only during CreateWindowExW
                return true;   // consumed → no default composition window
            case WM_IME_COMPOSITION:
                _textInput?.OnComposition(lp);
                return true;
            case WM_IME_ENDCOMPOSITION:
                _textInput?.OnEndComposition();
                return false;  // let DefWindowProc finish the IME teardown
        }
        return false;
    }

    // GET_POINTERID_WPARAM (winuser.h): the pointer id is the LOW word of wParam on every WM_(NC)POINTER* message.
    private static uint GET_POINTERID_WPARAM(WPARAM wParam) => (uint)((nuint)wParam & 0xFFFF);

    // One coalesced WM_POINTERUPDATE: drain the OS-side history (highest-rate samples the pump missed) OLDEST→newest so
    // the ring re-coalesces to the latest per contact. One screen→client→DIP convert per sample (never double-converted).
    private void PointerUpdate(uint pointerId)
    {
        POINTER_INFO* hist = stackalloc POINTER_INFO[PointerHistoryMax];
        uint count = PointerHistoryMax;
        if (GetPointerInfoHistory(pointerId, &count, hist) && count > 0)
        {
            if (count > PointerHistoryMax) count = PointerHistoryMax;
            for (int i = (int)count - 1; i >= 0; i--)   // history is newest-first; emit oldest→newest
                EmitPointerMove(in hist[i]);
            return;
        }
        POINTER_INFO pi;
        if (GetPointerInfo(pointerId, &pi)) EmitPointerMove(in pi);
    }

    private void EmitPointerMove(in POINTER_INFO pi)
    {
        Decode(in pi, out PointerKind kind, out float pressure, out uint time);
        var dipPos = ScreenPtToDip(pi.ptPixelLocation);
        if (FluentGpu.Foundation.ScrollLog.On && kind != PointerKind.Mouse)
            FluentGpu.Foundation.ScrollLog.Line($"MOVE {kind} id={pi.pointerId} pos=({dipPos.X:0},{dipPos.Y:0})");
        _queue.Enqueue(new InputEvent(InputKind.PointerMove, dipPos, 0, 0,
            Mods: Mods(), Pointer: kind, TimestampMs: time, PointerId: pi.pointerId, Pressure: pressure));
    }

    // WM_POINTERDOWN/UP. Touch/pen "down" is the primary action (button 0); a PT_MOUSE contact reports which physical
    // button transitioned via ButtonChangeType so right/middle clicks survive. Implicit contact→window capture means no
    // SetCapture refcount: the OS streams this contact's updates+up to us until it breaks contact.
    private void PointerDownUp(uint pointerId, bool down)
    {
        POINTER_INFO pi;
        if (!GetPointerInfo(pointerId, &pi)) return;
        Decode(in pi, out PointerKind kind, out float pressure, out uint time);
        int button = kind == PointerKind.Mouse ? ButtonForChange(pi.ButtonChangeType) : 0;
        var dipPos = ScreenPtToDip(pi.ptPixelLocation);
        if (FluentGpu.Foundation.ScrollLog.On && kind != PointerKind.Mouse)
            FluentGpu.Foundation.ScrollLog.Line($"{(down ? "DOWN" : "UP  ")} {kind} id={pointerId} pos=({dipPos.X:0},{dipPos.Y:0})");
        _queue.Enqueue(new InputEvent(down ? InputKind.PointerDown : InputKind.PointerUp, dipPos,
            button, 0, Mods: Mods(), Pointer: kind, TimestampMs: time, PointerId: pointerId, Pressure: pressure));
    }

    // ── windowed-popup pointer forwarding (Win32PopupWindow → owner) ─────────────────────────────────────────────────
    // A windowed (out-of-bounds) popup HWND owns its own message pump but no dispatcher; under mouse-in-pointer ITS input
    // also arrives as WM_POINTER* (the retired WM_MOUSE* never fires), so it forwards the pointer stream here. The owner
    // re-decodes from the shared OS pointer id and converts SCREEN px → OWNER DIP (ScreenPtToDip targets the owner hwnd),
    // so the single dispatcher hit-tests the popup subtree at its scene coordinates exactly like the in-window path.
    internal void ForwardPopupPointerUpdate(uint pointerId) => PointerUpdate(pointerId);
    internal void ForwardPopupPointerDownUp(uint pointerId, bool down) => PointerDownUp(pointerId, down);

    // WM_POINTERWHEEL/HWHEEL over the popup: HIWORD(wParam) = signed notch (×120), lParam = SCREEN px — same sign/DIP
    // semantics as the owner's own wheel path, mapped to owner DIP so the forwarded wheel feels identical.
    internal void ForwardPopupPointerWheel(uint pointerId, long lp, short notch, bool horizontal)
    {
        float notchY = horizontal ? 0f : -(notch / 120f);
        float notchX = horizontal ? (notch / 120f) : 0f;
        float dipY = notchY * WheelDipPerNotch;
        float dipX = notchX * WheelDipPerNotch;
        _queue.Enqueue(new InputEvent(InputKind.Wheel, WheelPt(lp), 0, 0, dipY, Mods(),
            Pointer: PointerKindOf(pointerId), TimestampMs: Now(), PointerId: pointerId, ScrollDeltaX: dipX, WheelNotch: notchY, WheelNotchX: notchX));
    }

    // WM_POINTERCAPTURECHANGED over the popup: the contact's implicit capture broke — cancel just THAT PointerId's
    // interaction in the dispatcher (no click), the same per-id cancel the owner emits for its own capture loss.
    internal void ForwardPopupPointerCancel(uint pointerId) =>
        _queue.Enqueue(new InputEvent(InputKind.PointerCancel, default, 0, 0,
            Pointer: PointerKindOf(pointerId), TimestampMs: Now(), PointerId: pointerId));

    // Classify a contact + read its normalized pressure and timestamp. PT_TOUCH/PT_PEN pressure is 0..1024 (0 = the
    // digitizer reports none → keep 1 like a mouse); dwTime may be 0 (injected/synthetic) → fall back to the message clock.
    private void Decode(in POINTER_INFO pi, out PointerKind kind, out float pressure, out uint time)
    {
        time = pi.dwTime != 0 ? pi.dwTime : Now();
        if (IsTouchpadDevice(in pi))
        {
            kind = PointerKind.Touchpad;
            pressure = 1f;
            return;
        }
        switch (pi.pointerType)
        {
            case PT_TOUCH:
            {
                kind = PointerKind.Touch;
                POINTER_TOUCH_INFO ti;
                pressure = GetPointerTouchInfo(pi.pointerId, &ti) && ti.pressure != 0 ? ti.pressure / 1024f : 1f;
                return;
            }
            case PT_PEN:
            {
                kind = PointerKind.Pen;
                POINTER_PEN_INFO pp;
                pressure = GetPointerPenInfo(pi.pointerId, &pp) && pp.pressure != 0 ? pp.pressure / 1024f : 1f;
                return;
            }
            case PT_TOUCHPAD:
                kind = PointerKind.Touchpad;
                pressure = 1f;
                return;
            default:
                kind = PointerKind.Mouse;
                pressure = 1f;
                return;
        }
    }

    // Device-class tag used by wheel/leave/capture. Some precision-touchpad stacks expose the message-level pointer type
    // as PT_MOUSE; sourceDevice -> POINTER_DEVICE_INFO is the physical-device authority in that case.
    private PointerKind PointerKindOf(uint pointerId)
    {
        POINTER_INFO pi;
        if (GetPointerInfo(pointerId, &pi))
        {
            if (IsTouchpadDevice(in pi)) return PointerKind.Touchpad;
            return pi.pointerType switch
            {
                PT_TOUCH => PointerKind.Touch,
                PT_PEN => PointerKind.Pen,
                _ => PointerKind.Mouse,
            };
        }
        uint type;
        if (!GetPointerType(pointerId, &type)) return PointerKind.Mouse;
        return type switch
        {
            PT_TOUCH => PointerKind.Touch,
            PT_PEN => PointerKind.Pen,
            PT_TOUCHPAD => PointerKind.Touchpad,
            _ => PointerKind.Mouse,
        };
    }

    private bool IsTouchpadDevice(in POINTER_INFO pi)
    {
        if (pi.pointerType == PT_TOUCHPAD) return true;
        if (pi.sourceDevice == 0) return false;
        if (_touchpadDeviceCache.TryGetValue(pi.sourceDevice, out bool cached)) return cached;
        POINTER_DEVICE_INFO device;
        bool touchpad = GetPointerDevice(pi.sourceDevice, &device) &&
                        device.pointerDeviceType == POINTER_DEVICE_TYPE_TOUCH_PAD;
        _touchpadDeviceCache[pi.sourceDevice] = touchpad;
        return touchpad;
    }

    private static int ButtonForChange(uint change) => change switch
    {
        POINTER_CHANGE_SECONDBUTTON_DOWN or POINTER_CHANGE_SECONDBUTTON_UP => 1,
        POINTER_CHANGE_THIRDBUTTON_DOWN or POINTER_CHANGE_THIRDBUTTON_UP => 2,
        _ => 0,   // FIRSTBUTTON (left) or no/other change
    };

    internal static KeyModifiers Mods()
    {
        KeyModifiers m = KeyModifiers.None;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) m |= KeyModifiers.Shift;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) m |= KeyModifiers.Ctrl;
        if ((GetKeyState(VK_MENU) & 0x8000) != 0) m |= KeyModifiers.Alt;
        if ((GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0) m |= KeyModifiers.Win;
        return m;
    }

    internal static uint Now() => unchecked((uint)GetMessageTime());

    // POINTER_INFO.ptPixelLocation and the WM_POINTER* lParam are both SCREEN px (DPI-aware window) — map to client, then
    // DIP, ONCE, so it matches the DIP scene bounds (the same screen→client→DIP path the wheel uses).
    private Point2 ScreenPtToDip(POINT screen)
    {
        ScreenToClient(_hwnd, &screen);
        float s = _scale <= 0f ? 1f : _scale;
        return new Point2(screen.x / s, screen.y / s);
    }

    // WM_POINTERWHEEL/HWHEEL lParam carries SCREEN coords — map to client, then DIP.
    private Point2 WheelPt(long lp)
    {
        POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
        ScreenToClient(_hwnd, &pt);
        float s = _scale <= 0f ? 1f : _scale;
        return new Point2(pt.x / s, pt.y / s);
    }

    // Upper bound on OS-coalesced samples drained per WM_POINTERUPDATE: a stackalloc slab (no heap), generous for a
    // 60-240 Hz frame against a >1 kHz digitizer; any excess is harmlessly clamped (the ring re-coalesces to the latest).
    private const int PointerHistoryMax = 64;
}
