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
/// measured (an empty rect: a backend with no bounds) never parks.</para>
/// <para>The general case (F118) is <see cref="CoveredByWindows"/>: any window, covered by the UNION of the opaque top-level windows
/// above it (the backend's win-event-driven tracker reports them, <see cref="FluentGpu.Pal.IPlatformWindow.CopyOccluderRectsPx"/>), by a
/// cheap rect test that never touches pixels. It feeds the same park as the fullscreen pop-out case.</para></summary>
public static class WindowCoverPolicy
{
    /// <summary>How far (physical px) the parent's outer rect may stick out of the child's before the parent counts as still
    /// visible. A MAXIMIZED window's outer rect overhangs its monitor by the invisible resize border (~8 px at 100%, ~16 px at
    /// 200%), so a maximized main window on the pop-out's monitor must still read as covered; a window that really is
    /// partly on another monitor overhangs by far more.</summary>
    public const float EdgeTolerancePx = 24f;

    /// <summary>The most occluders <see cref="CoveredByWindows"/> considers (the rest are ignored, which can only report LESS
    /// coverage). Bounds the work and the stack: the grid is at most (2n+2) by (2n+2) cells.</summary>
    public const int MaxOccluders = 16;

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

    /// <summary>F118: true when <paramref name="targetPx"/> is completely hidden behind the UNION of
    /// <paramref name="occludersPx"/> (the visible rects of the opaque top-level windows above it; physical virtual-screen px).
    /// The target is first shrunk by <paramref name="tolerancePx"/> on every side (clamped to a quarter of its smaller side), the
    /// same slack <see cref="Covers"/> gives a maximized window's invisible resize border, so a window's outer rect overhanging
    /// its visible frame never keeps it "uncovered". What is left must be covered by the occluders: one occluder containing it is
    /// the fast path; otherwise the shrunk target is cut along every occluder edge into a small grid and each cell's centre must
    /// lie inside some occluder (exact for axis-aligned rects, no pixels read). An empty target, no occluders, or an
    /// occluder that is empty never covers; a union with any gap (two half-screen windows with a seam wider than the
    /// tolerance, an L-shaped cover) is NOT covered. At most <see cref="MaxOccluders"/> occluders are used. Allocation-free.</summary>
    public static bool CoveredByWindows(in RectF targetPx, ReadOnlySpan<RectF> occludersPx, float tolerancePx = EdgeTolerancePx)
    {
        if (targetPx.W <= 1f || targetPx.H <= 1f || occludersPx.IsEmpty) return false;
        float inset = MathF.Max(0f, MathF.Min(tolerancePx, MathF.Min(targetPx.W, targetPx.H) * 0.25f));
        float tx0 = targetPx.X + inset, ty0 = targetPx.Y + inset;
        float tx1 = targetPx.X + targetPx.W - inset, ty1 = targetPx.Y + targetPx.H - inset;

        int n = Math.Min(occludersPx.Length, MaxOccluders);
        Span<float> ox0 = stackalloc float[MaxOccluders], oy0 = stackalloc float[MaxOccluders];
        Span<float> ox1 = stackalloc float[MaxOccluders], oy1 = stackalloc float[MaxOccluders];
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            RectF o = occludersPx[i];
            if (o.W <= 0f || o.H <= 0f) continue;
            float x0 = MathF.Max(o.X, tx0), y0 = MathF.Max(o.Y, ty0);
            float x1 = MathF.Min(o.X + o.W, tx1), y1 = MathF.Min(o.Y + o.H, ty1);
            if (x1 <= x0 || y1 <= y0) continue;   // misses the target (or only touches it)
            if (x0 <= tx0 && y0 <= ty0 && x1 >= tx1 && y1 >= ty1) return true;   // one window hides all of it
            ox0[used] = x0; oy0[used] = y0; ox1[used] = x1; oy1[used] = y1;
            used++;
        }
        if (used < 2) return false;   // a single occluder that did not contain the target leaves a gap

        Span<float> xs = stackalloc float[2 + 2 * MaxOccluders], ys = stackalloc float[2 + 2 * MaxOccluders];
        int nx = 0, ny = 0;
        xs[nx++] = tx0; xs[nx++] = tx1; ys[ny++] = ty0; ys[ny++] = ty1;
        for (int i = 0; i < used; i++)
        {
            xs[nx++] = ox0[i]; xs[nx++] = ox1[i];
            ys[ny++] = oy0[i]; ys[ny++] = oy1[i];
        }
        xs[..nx].Sort();
        ys[..ny].Sort();
        for (int xi = 0; xi + 1 < nx; xi++)
        {
            if (xs[xi + 1] <= xs[xi]) continue;
            float cx = (xs[xi] + xs[xi + 1]) * 0.5f;
            for (int yi = 0; yi + 1 < ny; yi++)
            {
                if (ys[yi + 1] <= ys[yi]) continue;
                float cy = (ys[yi] + ys[yi + 1]) * 0.5f;
                bool hit = false;
                for (int k = 0; k < used; k++)
                    if (cx >= ox0[k] && cx <= ox1[k] && cy >= oy0[k] && cy <= oy1[k]) { hit = true; break; }
                if (!hit) return false;   // a cell nobody covers: the window shows through there
            }
        }
        return true;
    }
}
