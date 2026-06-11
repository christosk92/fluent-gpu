using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// A borderless, non-activating, owned top-level popup HWND — the Win32 backing of <see cref="IPlatformPopupWindow"/>
/// and the engine analogue of WinUI's windowed <c>CPopup</c> (microsoft-ui-xaml Popup_Partial.cpp:1019
/// <c>SetIsWindowed()</c> creates an HWND via PopupSiteBridge so flyouts/menus can render OUTSIDE the XAML window;
/// Popup_Partial.cpp:951-970 <c>IsConstrainedToRootBounds == false</c> for windowed popups).
/// <list type="bullet">
/// <item><c>WS_POPUP</c> + owner: borderless, z-ordered above the owner, hidden when the owner minimizes.</item>
/// <item><c>WS_EX_NOACTIVATE</c> + <c>MA_NOACTIVATE</c>: the popup NEVER takes activation — keyboard focus stays on the
///   owner window exactly like WinUI's popup site (the dispatcher keeps routing keys to the main window).</item>
/// <item><c>WS_EX_TOOLWINDOW</c>: no taskbar button / Alt-Tab entry.</item>
/// <item><c>WS_EX_NOREDIRECTIONBITMAP</c>: no GDI redirection surface — the host presents into it with its own
///   composition swapchain (same composited-surface model as the main window).</item>
/// <item>Mouse input over the popup is FORWARDED to the owner <see cref="Win32Window"/>, translated popup-client →
///   screen → owner-client → owner DIP, so the one InputDispatcher hit-tests the popup subtree at its scene
///   coordinates (the subtree stays in the single SceneStore; out-of-bounds coords hit-test fine).</item>
/// </list>
/// </summary>
public sealed unsafe class Win32PopupWindow : IPlatformPopupWindow
{
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F,
                       WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200,
                       WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
                       WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
                       WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int MA_NOACTIVATE = 3;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int GWLP_USERDATA = -21;
    private const int IDC_ARROW = 32512;
    // DIP scrolled per wheel notch — must match Win32Window.WheelDipPerNotch so forwarded wheel feels identical.
    private const float WheelDipPerNotch = 48f;

    private const string ClassName = "FluentGpuPopupWindow";
    private static ushort s_atom;
    [ThreadStatic] private static Win32PopupWindow? s_constructing;

    private readonly HWND _hwnd;
    private readonly HWND _owner;
    private readonly GCHandle _self;
    private RectF _boundsPx;
    private bool _shown;
    private bool _disposed;

    public Win32PopupWindow(in PopupWindowDesc desc)
    {
        _owner = (HWND)desc.Owner.Value;
        _self = GCHandle.Alloc(this);
        HINSTANCE hinst = GetModuleHandleW(null);

        if (s_atom == 0)
        {
            fixed (char* cn = ClassName)
            {
                WNDCLASSEXW wc = default;
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.lpfnWndProc = &StaticWndProc;
                wc.hInstance = hinst;
                wc.hCursor = LoadCursorW(default, (char*)IDC_ARROW);
                wc.lpszClassName = cn;
                s_atom = RegisterClassExW(&wc);
            }
        }

        var b = desc.BoundsPx;
        int w = Math.Max(1, (int)MathF.Round(b.W)), h = Math.Max(1, (int)MathF.Round(b.H));
        s_constructing = this;
        fixed (char* cn = ClassName)
        {
            _hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
                cn, null, WS_POPUP,
                (int)MathF.Round(b.X), (int)MathF.Round(b.Y), w, h,
                _owner, HMENU.NULL, hinst, (void*)GCHandle.ToIntPtr(_self));
        }
        s_constructing = null;
        _boundsPx = new RectF(MathF.Round(b.X), MathF.Round(b.Y), w, h);
    }

    public NativeHandle Handle => new(_hwnd, NativeHandleKind.Hwnd);
    public RectF BoundsPx => _boundsPx;
    public bool IsShown => _shown;

    public void SetBoundsPx(in RectF px)
    {
        int w = Math.Max(1, (int)MathF.Round(px.W)), h = Math.Max(1, (int)MathF.Round(px.H));
        _boundsPx = new RectF(MathF.Round(px.X), MathF.Round(px.Y), w, h);
        SetWindowPos(_hwnd, HWND.NULL, (int)_boundsPx.X, (int)_boundsPx.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Show()
    {
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);   // never activates — focus stays on the owner (WS_EX_NOACTIVATE)
        _shown = true;
    }

    public void Hide()
    {
        ShowWindow(_hwnd, SW_HIDE);
        _shown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shown = false;
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
        Win32PopupWindow? self = ud != 0 ? GCHandle.FromIntPtr(ud).Target as Win32PopupWindow : s_constructing;
        if (self is not null && self.HandleMsg(hWnd, msg, wParam, lParam, out LRESULT r)) return r;
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool HandleMsg(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = default;
        long lp = (long)(nint)lParam;
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                result = (LRESULT)MA_NOACTIVATE;   // clicking the popup never activates it (WinUI popup-site behavior)
                return true;
            case WM_ERASEBKGND:
                result = (LRESULT)1;               // the host paints every pixel — suppress the flicker-erase
                return true;
            case WM_PAINT:
                ValidateRect(hWnd, null);
                return true;
            case WM_DESTROY:
                return true;
            case WM_MOUSEMOVE:
                Forward(InputKind.PointerMove, 0, lp, clientCoords: true);
                return true;
            case WM_LBUTTONDOWN: Forward(InputKind.PointerDown, 0, lp, clientCoords: true); return true;
            case WM_LBUTTONUP: Forward(InputKind.PointerUp, 0, lp, clientCoords: true); return true;
            case WM_RBUTTONDOWN: Forward(InputKind.PointerDown, 1, lp, clientCoords: true); return true;
            case WM_RBUTTONUP: Forward(InputKind.PointerUp, 1, lp, clientCoords: true); return true;
            case WM_MBUTTONDOWN: Forward(InputKind.PointerDown, 2, lp, clientCoords: true); return true;
            case WM_MBUTTONUP: Forward(InputKind.PointerUp, 2, lp, clientCoords: true); return true;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                // Wheel lParam is SCREEN coords (unlike the client-coord button messages).
                if (ResolveOwner() is not { } owner) return true;
                short notch = unchecked((short)((ulong)(nuint)wParam >> 16));
                float dip = -(notch / 120f) * WheelDipPerNotch;
                owner.EnqueueExternal(new InputEvent(InputKind.Wheel, ScreenToOwnerDip(owner, (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)),
                    0, 0, dip, Win32Window.Mods(), TimestampMs: Win32Window.Now()));
                return true;
            }
        }
        return false;
    }

    /// <summary>Forward a mouse message to the OWNER window's input queue, translated popup-client → owner-client DIP,
    /// so the single dispatcher hit-tests the popup subtree at its (possibly out-of-window) scene coordinates.</summary>
    private void Forward(InputKind kind, int button, long lp, bool clientCoords)
    {
        if (ResolveOwner() is not { } owner) return;
        POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
        if (clientCoords) ClientToScreen(_hwnd, &pt);
        owner.EnqueueExternal(new InputEvent(kind, ScreenToOwnerDip(owner, pt.x, pt.y), button, 0,
            Mods: Win32Window.Mods(), TimestampMs: Win32Window.Now()));
    }

    private static Point2 ScreenToOwnerDip(Win32Window owner, int sx, int sy)
    {
        POINT pt = new() { x = sx, y = sy };
        ScreenToClient(owner.Hwnd, &pt);
        float s = owner.ScaleInternal <= 0f ? 1f : owner.ScaleInternal;
        return new Point2(pt.x / s, pt.y / s);
    }

    /// <summary>Resolve the owner <see cref="Win32Window"/> from its HWND's GWLP_USERDATA GCHandle (the same slot the
    /// owner's own WndProc uses) — no extra registry needed.</summary>
    private Win32Window? ResolveOwner()
    {
        if (_owner == HWND.NULL) return null;
        nint ud = GetWindowLongPtrW(_owner, GWLP_USERDATA);
        return ud != 0 ? GCHandle.FromIntPtr(ud).Target as Win32Window : null;
    }
}
