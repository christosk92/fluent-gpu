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
/// <item>Pointer input over the popup (mouse/touch/pen — under the owner's process-wide mouse-in-pointer the retired
///   WM_MOUSE* never fires here, so the popup runs the WM_POINTER* stream too) is FORWARDED to the owner
///   <see cref="Win32Window"/>, which re-decodes the shared OS pointer id and maps SCREEN px → owner DIP, so the one
///   InputDispatcher hit-tests the popup subtree at its scene coordinates (the subtree stays in the single SceneStore;
///   out-of-bounds coords hit-test fine).</item>
/// </list>
/// </summary>
public sealed unsafe class Win32PopupWindow : IPlatformPopupWindow
{
    private const uint WM_NCCREATE = 0x0081, WM_DESTROY = 0x0002, WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F,
                       WM_MOUSEACTIVATE = 0x0021;
    // Pointer-input stream (the owner enables mouse-in-pointer process-wide, which is irreversible — Win32Platform.cs:163):
    // the popup's mouse/touch/pen ALL arrive as WM_POINTER* (the retired WM_MOUSE* client messages never fire here), so
    // the forwarding path is keyed on these exactly like the owner window. wParam LOW word = pointer id on every message.
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247,
                       WM_POINTERCAPTURECHANGED = 0x024C, WM_POINTERWHEEL = 0x024E, WM_POINTERHWHEEL = 0x024F;
    private const int MA_NOACTIVATE = 3;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int GWLP_USERDATA = -21;
    private const int IDC_ARROW = 32512;

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
            // Pointer stream → owner: the owner re-decodes from the shared OS pointer id (kind/pressure/timestamp) and
            // converts SCREEN px → OWNER DIP, so the single dispatcher hit-tests the popup subtree at its (out-of-window)
            // scene coordinates. The popup only routes by message type + pointer id; the owner owns the decode + convert.
            case WM_POINTERUPDATE:
                if (ResolveOwner() is { } upd) upd.ForwardPopupPointerUpdate(GET_POINTERID_WPARAM(wParam));
                return true;
            case WM_POINTERDOWN:
                if (ResolveOwner() is { } dn) dn.ForwardPopupPointerDownUp(GET_POINTERID_WPARAM(wParam), down: true);
                return true;
            case WM_POINTERUP:
                if (ResolveOwner() is { } up) up.ForwardPopupPointerDownUp(GET_POINTERID_WPARAM(wParam), down: false);
                return true;
            case WM_POINTERCAPTURECHANGED:
                if (ResolveOwner() is { } cap) cap.ForwardPopupPointerCancel(GET_POINTERID_WPARAM(wParam));
                return true;
            case WM_POINTERWHEEL:
            case WM_POINTERHWHEEL:
            {
                // HIWORD(wParam) = signed notch (×120); lParam = SCREEN px (same as the owner's own wheel path).
                if (ResolveOwner() is not { } owner) return true;
                short notch = unchecked((short)((ulong)(nuint)wParam >> 16));
                owner.ForwardPopupPointerWheel(GET_POINTERID_WPARAM(wParam), lp, notch, horizontal: msg == WM_POINTERHWHEEL);
                return true;
            }
        }
        return false;
    }

    // GET_POINTERID_WPARAM (winuser.h): the pointer id is the LOW word of wParam on every WM_POINTER* message.
    private static uint GET_POINTERID_WPARAM(WPARAM wParam) => (uint)((nuint)wParam & 0xFFFF);

    /// <summary>Resolve the owner <see cref="Win32Window"/> from its HWND's GWLP_USERDATA GCHandle (the same slot the
    /// owner's own WndProc uses) — no extra registry needed.</summary>
    private Win32Window? ResolveOwner()
    {
        if (_owner == HWND.NULL) return null;
        nint ud = GetWindowLongPtrW(_owner, GWLP_USERDATA);
        return ud != 0 ? GCHandle.FromIntPtr(ud).Target as Win32Window : null;
    }
}
