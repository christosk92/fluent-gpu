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

    public Win32App() => SetProcessDpiAwarenessContext(unchecked((nint)(-4)));

    public IPlatformWindow CreateWindow(in WindowDesc desc) => new Win32Window(desc);
    public void Dispose() { }
}

public sealed unsafe class Win32Window : IPlatformWindow
{
    // Win32 ABI constants (stable; defined locally to avoid TerraFX's per-prefix constant classes).
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SIZE = 0x0005,
                       WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E,
                       WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
                       WM_CHAR = 0x0102;
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
    private int _w, _h;
    private float _scale = 1f;
    private bool _closed;

    public Win32Window(in WindowDesc desc)
    {
        float requestedW = MathF.Max(1f, desc.SizePx.Width);
        float requestedH = MathF.Max(1f, desc.SizePx.Height);
        _w = (int)requestedW;
        _h = (int)requestedH;
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
        AdjustWindowRectEx(&rc, WS_OVERLAPPEDWINDOW, false, 0);

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

        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1f : dpi / 96f;
        ResizeClientPhysical((int)MathF.Round(requestedW * _scale), (int)MathF.Round(requestedH * _scale));

        RefreshClientSize();
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
        AdjustWindowRectEx(&rc, WS_OVERLAPPEDWINDOW, false, 0);
        SetWindowPos(_hwnd, HWND.NULL, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        RefreshClientSize();
    }

    public void SetTitle(StringId title) { }
    public void SetCursor(CursorId id) { }

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
                PaintRequested?.Invoke();   // keep the window live during the modal resize loop
                return true;
            case WM_PAINT:
                PaintRequested?.Invoke();
                ValidateRect(hWnd, null);
                return true;
            case WM_MOUSEMOVE: _queue.Enqueue(new InputEvent(InputKind.PointerMove, MousePt(lp), 0, 0)); return true;
            case WM_LBUTTONDOWN: _queue.Enqueue(new InputEvent(InputKind.PointerDown, MousePt(lp), 0, 0)); return true;
            case WM_LBUTTONUP: _queue.Enqueue(new InputEvent(InputKind.PointerUp, MousePt(lp), 0, 0)); return true;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                // HIWORD(wParam) = signed notch delta (×120). Win32: +delta = wheel forward = scroll up = offset↓,
                // so flip the sign to our "positive = toward content end" convention.
                short notch = unchecked((short)((ulong)(nuint)wParam >> 16));
                float dip = -(notch / 120f) * WheelDipPerNotch;
                _queue.Enqueue(new InputEvent(InputKind.Wheel, WheelPt(lp), 0, 0, dip));
                return true;
            }
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                _queue.Enqueue(new InputEvent(InputKind.Key, default, 0, (int)(nuint)wParam));
                return true;
            case WM_CHAR:
                // TranslateMessage (run in the pump) synthesizes WM_CHAR from WM_KEYDOWN → the layout/IME-resolved
                // codepoint, carried in the InputEvent.KeyCode slot. Editing/navigation keys still arrive via WM_KEYDOWN.
                _queue.Enqueue(new InputEvent(InputKind.Char, default, 0, (int)(nuint)wParam));
                return true;
        }
        return false;
    }

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
