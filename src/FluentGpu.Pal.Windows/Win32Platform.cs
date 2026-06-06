using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>Real Win32 PAL: <c>CreateWindowExW</c> + a static <c>WndProc</c> + a PeekMessage pump that drains WM_* into POD input.</summary>
public sealed unsafe class Win32App : IPlatformApp
{
    public IPlatformWindow CreateWindow(in WindowDesc desc) => new Win32Window(desc);
    public void Dispose() { }
}

public sealed unsafe class Win32Window : IPlatformWindow
{
    // Win32 ABI constants (stable; defined locally to avoid TerraFX's per-prefix constant classes).
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SIZE = 0x0005,
                       WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOW = 5;
    private const uint PM_REMOVE = 0x0001;
    private const int GWLP_USERDATA = -21;
    private const int IDC_ARROW = 32512;

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
        _w = (int)desc.SizePx.Width;
        _h = (int)desc.SizePx.Height;
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
                0, cn, title, WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, rc.right - rc.left, rc.bottom - rc.top,
                HWND.NULL, HMENU.NULL, hinst, (void*)GCHandle.ToIntPtr(_self));
        }
        s_constructing = null;

        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1f : dpi / 96f;

        RECT cr;
        GetClientRect(_hwnd, &cr);
        _w = cr.right - cr.left;
        _h = cr.bottom - cr.top;
    }

    public NativeHandle Handle => new(_hwnd, NativeHandleKind.Hwnd);
    public Size2 ClientSizePx => new(_w, _h);
    public float Scale => _scale;
    public bool IsClosed => _closed;

    public void Show()
    {
        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);
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
        if (self is not null && self.Handle32(hWnd, msg, lParam, out LRESULT r)) return r;
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool Handle32(HWND hWnd, uint msg, LPARAM lParam, out LRESULT result)
    {
        result = default;
        long lp = (long)(nint)lParam;
        switch (msg)
        {
            case WM_DESTROY: _closed = true; PostQuitMessage(0); return true;
            case WM_CLOSE: _closed = true; DestroyWindow(hWnd); return true;
            case WM_SIZE: _w = (int)(lp & 0xFFFF); _h = (int)((lp >> 16) & 0xFFFF); return true;
            case WM_MOUSEMOVE: _queue.Enqueue(new InputEvent(InputKind.PointerMove, MousePt(lp), 0, 0)); return true;
            case WM_LBUTTONDOWN: _queue.Enqueue(new InputEvent(InputKind.PointerDown, MousePt(lp), 0, 0)); return true;
            case WM_LBUTTONUP: _queue.Enqueue(new InputEvent(InputKind.PointerUp, MousePt(lp), 0, 0)); return true;
        }
        return false;
    }

    private static Point2 MousePt(long lp) => new((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
}
