using FluentGpu.Foundation;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>What a restored main-window placement resolves to on this machine right now (<see cref="Win32Placement.Resolve"/>):
/// the outer rect the window is created at, whether it opens maximized, whether the clamp moved the saved rect, the target
/// monitor's DPI scale, and the client size the first frame lays out at.</summary>
/// <param name="BoundsPx">The NORMAL outer rect, physical virtual-screen px, after the clamp (the restore rect when maximized).</param>
/// <param name="Maximized">The window is created maximized on the monitor <paramref name="BoundsPx"/> is on.</param>
/// <param name="Clamped">The clamp changed the saved rect (its monitor is gone, the work area shrank, a DPI or resolution change).</param>
/// <param name="DpiScale">The target monitor's DPI scale (1 = 96 DPI): the window's raw scale at creation.</param>
/// <param name="ClientSizeDip">The client size at creation in raw DIP (physical px / <paramref name="DpiScale"/>, app zoom excluded).</param>
/// <param name="WorkAreaPx">The target monitor's work area, physical px (infinite when unknown).</param>
public readonly record struct WindowRestorePlan(RectF BoundsPx, bool Maximized, bool Clamped, float DpiScale, Size2 ClientSizeDip, RectF WorkAreaPx);

/// <summary>The Win32 half of a restored window placement: resolving a saved rect against today's monitors before the window
/// exists, and reading a live window's normal placement back (<c>GetWindowPlacement</c> semantics).</summary>
public static unsafe class Win32Placement
{
    /// <summary>Resolve a saved placement against the monitors as they are now: clamp <paramref name="savedPx"/> into the work
    /// area of the monitor nearest it (<see cref="WindowPlacementRules.ClampIntoWorkArea"/>, the detached windows' rule), read that
    /// monitor's DPI, and predict the client size the window gets there (normal: the outer rect minus the frame; maximized: the work
    /// area, minus the caption for a standard frame). The main window's creation runs exactly this, so an app can seed anything
    /// that depends on the first frame's size (the app zoom) from <see cref="WindowRestorePlan.ClientSizeDip"/> with no relayout
    /// after the first frame. Sets the process DPI awareness the engine uses (idempotent) so it reads physical px. Any thread.</summary>
    public static WindowRestorePlan Resolve(RectF savedPx, bool maximized, bool customFrame, bool resizable = true, bool toolWindow = false)
    {
        Win32App.EnsureDpiAwareness();
        var work = Win32App.WorkAreaAt(WindowPlacementRules.Anchor(savedPx));
        var bounds = WindowPlacementRules.ClampIntoWorkArea(savedPx, work);
        bounds = new RectF(MathF.Round(bounds.X), MathF.Round(bounds.Y), MathF.Round(bounds.W), MathF.Round(bounds.H));
        bool clamped = MathF.Abs(bounds.X - MathF.Round(savedPx.X)) >= 1f || MathF.Abs(bounds.Y - MathF.Round(savedPx.Y)) >= 1f
            || MathF.Abs(bounds.W - MathF.Round(savedPx.W)) >= 1f || MathF.Abs(bounds.H - MathF.Round(savedPx.H)) >= 1f;

        uint dpi = 96;
        POINT pt = new() { x = (int)(bounds.X + bounds.W * 0.5f), y = (int)(bounds.Y + bounds.H * 0.5f) };
        HMONITOR mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        uint dx = 96, dy = 96;
        if (mon != HMONITOR.NULL && GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dx, &dy) >= 0 && dx != 0) dpi = dx;
        float scale = dpi / 96f;

        Win32Window.FrameInsets(customFrame, resizable, toolWindow, dpi, out int left, out int top, out int right, out int bottom);
        float cw, ch;
        if (maximized && !work.IsInfinite)
        {
            // A maximized window hangs its sizing frame off every edge of the work area: the client is the work area, minus the
            // caption a standard frame keeps (top inset minus the frame thickness the bottom inset is).
            cw = work.W;
            ch = work.H - (customFrame ? 0 : top - bottom);
        }
        else
        {
            cw = bounds.W - left - right;
            ch = bounds.H - top - bottom;
        }
        return new WindowRestorePlan(bounds, maximized, clamped, scale,
            new Size2(MathF.Max(1f, cw) / scale, MathF.Max(1f, ch) / scale), work);
    }

    /// <summary><c>GetWindowPlacement</c>'s <c>rcNormalPosition</c> in screen px. It comes back in WORKSPACE coordinates for a
    /// top-level window without <c>WS_EX_TOOLWINDOW</c> (offset by the work area's inset from the monitor's top-left on the
    /// monitor it lies on), so the inset of that monitor is added back.</summary>
    internal static RectF NormalRectScreenPx(HWND hwnd, bool toolWindow, out bool restoreToMaximized)
    {
        WINDOWPLACEMENT wp = default;
        wp.length = (uint)sizeof(WINDOWPLACEMENT);
        restoreToMaximized = false;
        if (!GetWindowPlacement(hwnd, &wp)) return default;
        restoreToMaximized = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
        RECT r = wp.rcNormalPosition;
        if (!toolWindow)
        {
            HMONITOR mon = MonitorFromRect(&r, 2 /* MONITOR_DEFAULTTONEAREST */);
            MONITORINFO mi = default;
            mi.cbSize = (uint)sizeof(MONITORINFO);
            if (mon != HMONITOR.NULL && GetMonitorInfoW(mon, &mi))
            {
                int ox = mi.rcWork.left - mi.rcMonitor.left, oy = mi.rcWork.top - mi.rcMonitor.top;
                r.left += ox; r.right += ox; r.top += oy; r.bottom += oy;
            }
        }
        return new RectF(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }
}
