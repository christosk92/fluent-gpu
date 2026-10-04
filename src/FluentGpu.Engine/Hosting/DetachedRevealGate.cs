using System.Diagnostics;

namespace FluentGpu.Hosting;

/// <summary>
/// The reveal rule of a detached pop-out window (F115). The window is created HIDDEN and shown exactly once, when the first
/// frame of its own swapchain has actually presented (<c>ISwapchain.HasPresentedContent</c>), or when a timeout says that frame
/// is not coming. The window is composited (no redirection bitmap), so a window shown before that frame is a hollow or
/// see-through topmost rectangle; the engine's own windowed popups use the same rule.
/// <para>While the reveal is pending the host is exempt from the hidden-window park (a hidden window parks like a minimized
/// one and would never paint the very frame the reveal waits for) and from the production gate; the timeout is the
/// fallback that makes the exemption bounded - a backend that never reports a presented frame still ends up with a visible,
/// normally-running window instead of an invisible one.</para>
/// </summary>
internal static class DetachedRevealGate
{
    /// <summary>How long a pop-out may stay hidden waiting for its first present before it is shown anyway (ms). A healthy open
    /// presents within a few frames; this only bounds a stuck render turn.</summary>
    internal const int TimeoutMs = 750;

    /// <summary>The wait (ms) a pending host asks the loop for, so the UI thread comes back to look at
    /// <c>HasPresentedContent</c> promptly even when nothing else is waking it (the present is acknowledged on the render
    /// thread, which does not wake the UI loop by itself).</summary>
    internal const int PollMs = 4;

    /// <summary>The QPC deadline of a reveal armed at <paramref name="startQpc"/> that may wait <paramref name="timeoutMs"/>.</summary>
    internal static long DeadlineQpc(long startQpc, int timeoutMs = TimeoutMs) => startQpc + timeoutMs * Stopwatch.Frequency / 1000;

    /// <summary>True when the window should be shown now: its first frame presented, or the deadline passed.</summary>
    internal static bool Due(bool presentedContent, long nowQpc, long deadlineQpc) => presentedContent || nowQpc >= deadlineQpc;
}
