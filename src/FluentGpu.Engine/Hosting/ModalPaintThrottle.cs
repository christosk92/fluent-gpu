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
}
