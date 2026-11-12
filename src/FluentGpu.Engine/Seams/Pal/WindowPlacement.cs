using FluentGpu.Foundation;

namespace FluentGpu.Pal;

/// <summary>Where a top-level window returns to, read live from the window: its NORMAL (restore) outer rect in physical
/// virtual-screen px and whether it is maximized, plus the transient states an app persisting it must skip. The normal
/// rect is the rect the window has when it is neither maximized nor minimized: <c>GetWindowPlacement</c>'s
/// <c>rcNormalPosition</c> while maximized or minimized, the live window rect otherwise (a snapped window reports the
/// snapped rect it shows), and the rect it had before entering borderless full screen while full screen.</summary>
/// <param name="NormalBoundsPx">The restore outer rect, physical virtual-screen px.</param>
/// <param name="Maximized">The window is maximized (or, while minimized or full screen, returns to maximized).</param>
/// <param name="Minimized">The window is minimized right now.</param>
/// <param name="Visible">The window is shown (false while hidden: a tray park, a mode that hides it).</param>
/// <param name="Fullscreen">The window is in borderless full screen right now.</param>
public readonly record struct WindowPlacement(RectF NormalBoundsPx, bool Maximized, bool Minimized, bool Visible, bool Fullscreen)
{
    /// <summary>On screen in the user's own placement: shown, not minimized, not full screen. The state an app persists.</summary>
    public bool IsSettledPlacement => Visible && !Minimized && !Fullscreen && NormalBoundsPx.W > 1f && NormalBoundsPx.H > 1f;
}

/// <summary>The one rule every restored window placement goes through (the main window's <c>InitialBoundsPx</c> and a
/// detached window's <c>DetachedWindowRequest.InitialBoundsPx</c>): a remembered rect is moved, and shrunk only when it is
/// larger than the area, into the work area of the monitor nearest its centre, so a placement remembered on a monitor
/// that has since been unplugged, or saved at another resolution or DPI, still opens fully reachable. The clamp is never
/// written back by the engine: plug the monitor in again and the remembered rect is whole again.</summary>
public static class WindowPlacementRules
{
    /// <summary>The point the work area is looked up from: the rect's centre.</summary>
    public static Point2 Anchor(RectF restoredPx) => new(restoredPx.X + restoredPx.W * 0.5f, restoredPx.Y + restoredPx.H * 0.5f);

    /// <summary><paramref name="restoredPx"/> moved (and shrunk where it is bigger) into <paramref name="workPx"/>.
    /// An infinite (unknown) work area returns the rect unchanged.</summary>
    public static RectF ClampIntoWorkArea(RectF restoredPx, RectF workPx)
    {
        if (workPx.IsInfinite) return restoredPx;
        float w = MathF.Min(restoredPx.W, workPx.W), h = MathF.Min(restoredPx.H, workPx.H);
        float x = MathF.Min(MathF.Max(restoredPx.X, workPx.X), workPx.X + workPx.W - w);
        float y = MathF.Min(MathF.Max(restoredPx.Y, workPx.Y), workPx.Y + workPx.H - h);
        return new RectF(x, y, w, h);
    }
}
