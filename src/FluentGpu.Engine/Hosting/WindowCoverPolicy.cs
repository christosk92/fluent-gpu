using FluentGpu.Foundation;

namespace FluentGpu.Hosting;

/// <summary>Pure verdict for F118: is the main window fully hidden behind a pop-out, so it should park like a minimized one?
/// The only stand-down the Win32 backend can see by itself is minimized / hidden / cloaked; a flip-model composition swapchain
/// does not reliably report DXGI_STATUS_OCCLUDED for a window another top-level covers, so a borderless fullscreen pop-out over
/// the main window left the main window recording and presenting invisible frames on the shared render thread.
/// <para>The narrow case this answers: the pop-out is fullscreen (it owns its monitor with the chrome removed), is the
/// ACTIVE window (so it is in front: alt-tab away makes it inactive, which un-parks the main window at once), is on screen, and
/// its rect contains the main window's whole outer rect. A parent that spans two monitors, or lives on another one, sticks
/// out of the child's rect and is never parked; a snapped or windowed pop-out is not fullscreen. Anything that cannot be
/// measured (an empty rect: a backend with no bounds) never parks. The general per-window occlusion tracker
/// (SetWinEventHook) is deliberately not built here.</para></summary>
public static class WindowCoverPolicy
{
    /// <summary>How far (physical px) the parent's outer rect may stick out of the child's before the parent counts as still
    /// visible. A MAXIMIZED window's outer rect overhangs its monitor by the invisible resize border (~8 px at 100%, ~16 px at
    /// 200%), so a maximized main window on the pop-out's monitor must still read as covered; a window that really is
    /// partly on another monitor overhangs by far more.</summary>
    public const float EdgeTolerancePx = 24f;

    /// <summary>True when the child pop-out hides the parent window completely. All rects are physical virtual-screen px (the
    /// window's <c>OuterBoundsPx</c>).</summary>
    public static bool Covers(bool childFullscreen, bool childActive, bool childVisible,
                              in RectF childBoundsPx, in RectF parentBoundsPx, float tolerancePx = EdgeTolerancePx)
    {
        if (!childFullscreen || !childActive || !childVisible) return false;
        if (childBoundsPx.W <= 1f || childBoundsPx.H <= 1f) return false;
        if (parentBoundsPx.W <= 1f || parentBoundsPx.H <= 1f) return false;
        return parentBoundsPx.X >= childBoundsPx.X - tolerancePx
            && parentBoundsPx.Y >= childBoundsPx.Y - tolerancePx
            && parentBoundsPx.X + parentBoundsPx.W <= childBoundsPx.X + childBoundsPx.W + tolerancePx
            && parentBoundsPx.Y + parentBoundsPx.H <= childBoundsPx.Y + childBoundsPx.H + tolerancePx;
    }
}
