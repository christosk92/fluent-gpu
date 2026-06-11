using System.Runtime.InteropServices;
using FluentGpu.Foundation;
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

    public void Dispose() { }
}

public sealed unsafe class Win32Window : IPlatformWindow
{
    // Win32 ABI constants (stable; defined locally to avoid TerraFX's per-prefix constant classes).
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SIZE = 0x0005,
                       WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
                       WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E,
                       WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
                       WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105,
                       WM_CHAR = 0x0102, WM_ACTIVATE = 0x0006, WM_SETCURSOR = 0x0020, WM_CAPTURECHANGED = 0x0215,
                       WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F,
                       WM_IME_SETCONTEXT = 0x0281;
    private const long ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000L;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int HTCLIENT = 1;
    // ── custom-frame (WindowDesc.CustomFrame) non-client constants ────────────────────────────────────────────────────
    private const uint WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086,
                       WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
                       WM_NCRBUTTONUP = 0x00A5, WM_NCMOUSELEAVE = 0x02A2, WM_SYSCOMMAND = 0x0112;
    private const int HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTCLOSE = 20;
    private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030,
                       SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
    private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const uint TME_LEAVE = 0x0002, TME_NONCLIENT = 0x0010;
    private const uint MF_BYCOMMAND = 0x0, MF_ENABLED = 0x0, MF_GRAYED = 0x1;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOSIZE = 0x0001;
    // DIP scrolled per wheel notch (120 units). ~3 lines × ~16px — the conventional Windows feel.
    private const float WheelDipPerNotch = 48f;
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

    private const string ClassName = "FluentGpuWindow";
    private static ushort s_atom;
    [ThreadStatic] private static Win32Window? s_constructing;

    private readonly HWND _hwnd;
    private readonly GCHandle _self;
    private readonly Queue<InputEvent> _queue = new();
    private Win32TextInput _textInput = null!;   // created right after the HWND exists (WndProc IME cases route to it)
    private int _w, _h;
    private float _scale = 1f;
    private bool _closed;

    // ── custom-frame state (WindowDesc.CustomFrame): engine-reported NC regions + NC-pointer synthesis ──────────────
    private readonly bool _customFrame;
    private TitleBarRegion[] _ncRegions = [];
    private int _ncRegionCount;
    private TitleBarHit _ncHover = TitleBarHit.Client;   // Client = "none of our buttons" sentinel in NC context
    private TitleBarHit _ncPress = TitleBarHit.Client;
    private bool _ncInside;       // pointer currently in the non-client area (clears stale engine hover on entry)
    private bool _ncTracking;     // TME_NONCLIENT leave-tracking armed
    private bool _active = true;  // WM_(NC)ACTIVATE — IsActive pull side
    private bool _wasZoomed;      // WM_SIZE edge-detect → InputKind.WindowStateChanged
    private static readonly Point2 OffscreenDip = new(-10000f, -10000f);

    public Win32Window(in WindowDesc desc)
    {
        float requestedW = MathF.Max(1f, desc.SizePx.Width);
        float requestedH = MathF.Max(1f, desc.SizePx.Height);
        _w = (int)requestedW;
        _h = (int)requestedH;
        _customFrame = desc.CustomFrame;   // must be set BEFORE CreateWindowExW: WM_NCCALCSIZE fires during creation
        _self = GCHandle.Alloc(this);

        HINSTANCE hinst = GetModuleHandleW(null);

        if (s_atom == 0)
        {
            fixed (char* cn = ClassName)
            {
                WNDCLASSEXW wc = default;
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.style = CS_HREDRAW | CS_VREDRAW;
                wc.lpfnWndProc = &StaticWndProc;
                wc.hInstance = hinst;
                wc.hCursor = LoadCursorW(default, (char*)IDC_ARROW);
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

    public void Dispose()
    {
        if (_hwnd != HWND.NULL) DestroyWindow(_hwnd);
        if (_self.IsAllocated) _self.Free();
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
            case WM_ERASEBKGND: result = (LRESULT)1; return true;   // we paint every pixel — suppress the flicker-erase
            case WM_SIZE:
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
                PaintRequested?.Invoke();   // keep the window live during the modal resize loop
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
            case WM_MOUSEMOVE:
                _ncInside = false; _ncHover = TitleBarHit.Client;   // back in the client area — real moves own hover again
                _queue.Enqueue(new InputEvent(InputKind.PointerMove, MousePt(lp), 0, 0, Mods: Mods(), TimestampMs: Now()));
                return true;
            case WM_LBUTTONDOWN: ButtonDown(0, lp); return true;
            case WM_LBUTTONUP: ButtonUp(0, lp); return true;
            case WM_RBUTTONDOWN: ButtonDown(1, lp); return true;
            case WM_RBUTTONUP: ButtonUp(1, lp); return true;
            case WM_MBUTTONDOWN: ButtonDown(2, lp); return true;
            case WM_MBUTTONUP: ButtonUp(2, lp); return true;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                // HIWORD(wParam) = signed notch delta (×120). Win32: +delta = wheel forward = scroll up = offset↓,
                // so flip the sign to our "positive = toward content end" convention.
                short notch = unchecked((short)((ulong)(nuint)wParam >> 16));
                float dip = -(notch / 120f) * WheelDipPerNotch;
                _queue.Enqueue(new InputEvent(InputKind.Wheel, WheelPt(lp), 0, 0, dip, Mods(), TimestampMs: Now()));
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
            case WM_SETCURSOR:
                // Re-assert the engine-chosen cursor while over the client area; let DefWindowProc style the chrome.
                if (((long)(nint)lParam & 0xFFFF) == HTCLIENT) { ApplyCursor(); result = (LRESULT)1; return true; }
                return false;
            case WM_CAPTURECHANGED:
                if (_buttonsDown != 0)
                {
                    _buttonsDown = 0;
                    _queue.Enqueue(new InputEvent(InputKind.PointerCancel, default, 0, 0, TimestampMs: Now()));
                }
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
                int button = HitTestRegions(pt.x, pt.y, buttonsOnly: true);
                if (button != 0) { result = (LRESULT)button; return true; }
                if (!IsZoomed(hWnd) && pt.y >= 0)
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

            // NC pointer → synthesized engine input: HTMIN/HTMAX/HTCLOSE pixels are non-client, so client WM_MOUSE*
            // never arrives there. Translating the NC stream into pointer events at the button's center drives the
            // engine-drawn buttons' InteractionAnimator ramps (hover/press/click) exactly like a real pointer.
            case WM_NCMOUSEMOVE when _customFrame:
            {
                TitleBarHit ncHit = NcHitFromCode((long)(nuint)wParam);
                if (!_ncTracking)
                {
                    TRACKMOUSEEVENT tme = new() { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hWnd };
                    TrackMouseEvent(&tme);
                    _ncTracking = true;
                }
                bool changed = !_ncInside;   // client → NC crossing: pull engine hover off whatever was hovered
                _ncInside = true;
                if (ncHit != _ncHover) { _ncHover = ncHit; changed = true; }
                if (changed)
                {
                    _queue.Enqueue(new InputEvent(InputKind.PointerMove,
                        ncHit == TitleBarHit.Client ? OffscreenDip : NcCenterDip(ncHit), 0, 0, Mods: Mods(), TimestampMs: Now()));
                    PaintRequested?.Invoke();   // NC interactions can arrive while the frame loop idles
                }
                if (ncHit != TitleBarHit.Client) { result = 0; return true; }   // eat over engine buttons (no classic NC UI)
                return false;
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

            case WM_NCLBUTTONDOWN when _customFrame:
            {
                TitleBarHit ncHit = NcHitFromCode((long)(nuint)wParam);
                if (ncHit == TitleBarHit.Client) return false;   // HTCAPTION/resize → DefWindowProc (OS drag-move/size loop, double-click maximize)
                _ncPress = ncHit;
                _queue.Enqueue(new InputEvent(InputKind.PointerDown, NcCenterDip(ncHit), 0, 0, Mods: Mods(), TimestampMs: Now()));
                PaintRequested?.Invoke();
                result = 0;
                return true;   // consumed: DefWindowProc would draw the classic NC button press
            }

            case WM_NCLBUTTONUP when _customFrame:
            {
                TitleBarHit ncHit = NcHitFromCode((long)(nuint)wParam);
                if (_ncPress == TitleBarHit.Client) return false;
                // Release on the pressed button = click (the engine OnClick invokes Minimize/ToggleMaximize/Close);
                // anywhere else = cancel (offscreen up clears the pressed state without firing).
                _queue.Enqueue(new InputEvent(InputKind.PointerUp,
                    ncHit == _ncPress ? NcCenterDip(_ncPress) : OffscreenDip, 0, 0, Mods: Mods(), TimestampMs: Now()));
                _ncPress = TitleBarHit.Client;
                PaintRequested?.Invoke();
                result = 0;
                return true;
            }

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
                    SetMenuDefaultItem(sys, SC_CLOSE, 0);
                    int cmd = (int)TrackPopupMenu(sys, TPM_RETURNCMD, (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF), 0, hWnd, null);
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

    private int _buttonsDown;   // bitmask by button index — capture is held while any button is down

    private void ButtonDown(int button, long lp)
    {
        if (_buttonsDown == 0) SetCapture(_hwnd);   // drags + drag-selection keep streaming off-window
        _buttonsDown |= 1 << button;
        _queue.Enqueue(new InputEvent(InputKind.PointerDown, MousePt(lp), button, 0, Mods: Mods(), TimestampMs: Now()));
    }

    private void ButtonUp(int button, long lp)
    {
        _buttonsDown &= ~(1 << button);
        if (_buttonsDown == 0) ReleaseCapture();
        _queue.Enqueue(new InputEvent(InputKind.PointerUp, MousePt(lp), button, 0, Mods: Mods(), TimestampMs: Now()));
    }

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

    // Pointer arrives in PHYSICAL px (DPI-aware window); convert to DIP so it matches the DIP scene bounds.
    private Point2 MousePt(long lp)
    {
        float s = _scale <= 0f ? 1f : _scale;
        return new Point2((short)(lp & 0xFFFF) / s, (short)((lp >> 16) & 0xFFFF) / s);
    }

    // WM_MOUSEWHEEL carries SCREEN coords (unlike WM_MOUSEMOVE's client coords) — map to client, then DIP.
    private Point2 WheelPt(long lp)
    {
        POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
        ScreenToClient(_hwnd, &pt);
        float s = _scale <= 0f ? 1f : _scale;
        return new Point2(pt.x / s, pt.y / s);
    }
}
