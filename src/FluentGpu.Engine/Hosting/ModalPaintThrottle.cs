namespace FluentGpu.Hosting;

/// <summary>Pure modal-loop paint throttle (TerraFX-free — testable from VerticalSlice without referencing
/// FluentGpu.Windows). Returns true when the paint should be skipped.</summary>
public static class ModalPaintThrottle
{
    /// <param name="nowMs"><see cref="Environment.TickCount64"/>.</param>
    /// <param name="lastMs">Last paint timestamp; updated when a paint is allowed.</param>
    /// <param name="sized">True once the modal loop has delivered WM_SIZE (edge resize).</param>
    /// <param name="minIntervalMs">Minimum ms between paints (~33 for 30 Hz).</param>
    /// <param name="forcePaint">When false, never throttle (pure move on non-composited uses per-step paints).</param>
    public static bool ShouldSkip(long nowMs, ref long lastMs, bool sized, int minIntervalMs, bool forcePaint = true)
    {
        if (!forcePaint || !sized) return false;
        if (nowMs - lastMs < minIntervalMs) return true;
        lastMs = nowMs;
        return false;
    }

    /// <summary>The cadence of the process-level peer tick (F093): while ONE window sits in the OS modal move/size loop, every
    /// OTHER live window is repainted at most this often (~30 Hz, the same floor an inactive window runs autonomous motion at).</summary>
    public const int PeerTickIntervalMs = 33;

    /// <summary>Pure throttle of the process-level peer tick. ONE timestamp is shared by every window of the process (the root
    /// host owns it), so the timer of the dragged window and the WM_SIZE of an edge resize together cost one peer round per
    /// interval, never one each. Returns true when this tick should be skipped; when it is allowed <paramref name="lastMs"/> is
    /// stamped. A clock that moved backwards (a wrapped or reset tick source) never starves the peers: it re-stamps and ticks.</summary>
    /// <param name="nowMs"><see cref="Environment.TickCount64"/>.</param>
    /// <param name="lastMs">Last allowed peer tick; 0 = none yet.</param>
    /// <param name="minIntervalMs">Minimum ms between peer ticks.</param>
    public static bool ShouldSkipPeerTick(long nowMs, ref long lastMs, int minIntervalMs = PeerTickIntervalMs)
    {
        if (lastMs != 0 && nowMs >= lastMs && nowMs - lastMs < minIntervalMs) return true;
        lastMs = nowMs;
        return false;
    }

    /// <summary>Which windows a peer tick repaints: every live window EXCEPT the one in the modal loop (it paints itself through
    /// its own keep-alive), one that is closed or parked (minimized / hidden / cloaked / covered paints nothing), and one that is
    /// itself in a modal loop (only one loop runs per thread, but never nest a paint inside another loop's own).</summary>
    public static bool ShouldPaintPeer(bool isSource, bool closed, bool parked, bool inModalLoop)
        => !isSource && !closed && !parked && !inModalLoop;
}
