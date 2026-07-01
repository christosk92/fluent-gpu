using FluentGpu.Rhi;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The render-thread seam's per-frame carrier — <b>Cut A (submit-only)</b> variant.
///
/// Canon (design/subsystems/threading-render-seam.md §2.1) specifies a Cut-B <c>SceneFrame</c> that carries a
/// <c>SnapshotColumns</c> of the scene so <i>record</i> runs on the render thread. Cut A keeps record on the UI thread
/// (it is already sub-ms + zero-alloc; the measured stall is in submit/present, not record) and instead carries the
/// <b>finished DrawList</b> across the seam: the UI records into the publisher's per-slot render-readable arena and
/// publishes this POD header naming that slot; the render thread submits + presents from it. This is the documented Cut-A
/// deviation from canon Cut B (see docs/plans/render-thread-seam-landing-plan.md §2).
///
/// Pure blittable POD — no GC references cross the seam. The DrawList bytes/sortkeys live in the render-readable arena
/// named by <see cref="ArenaIndex"/>; the render thread reads them by <c>(ArenaIndex, ByteLen/SortLen)</c>.
/// </summary>
public struct RenderFrame
{
    /// <summary>Monotonic publish sequence — the happens-before token (§2) and the quarantine key (§5).</summary>
    public ulong PublishSeq;

    /// <summary>Which publisher slot's arena holds this frame's bytes — read via <see cref="SceneFramePublisher.Bytes"/> / <see cref="SceneFramePublisher.SortKeys"/>.</summary>
    public int ArenaIndex;

    /// <summary>Valid prefix length (bytes) of the arena's command buffer.</summary>
    public int ByteLen;

    /// <summary>Valid prefix length (elements) of the arena's sort-key buffer.</summary>
    public int SortLen;

    /// <summary>POD submit context (target size, DPI scale, clear color, damage) — the <see cref="IGpuDevice.SubmitDrawList(System.ReadOnlySpan{byte},System.ReadOnlySpan{ulong},in FrameInfo)"/> args.</summary>
    public FrameInfo Submit;

    /// <summary>This frame was a modal-loop / live-resize repaint: suppress the present-latency wait so the present is a
    /// cheap tear-free hand-off (the <c>keepAlive</c> path). Applied on the render thread just before submit so the
    /// vsync-suppress (a ComPtr touch) is render-thread-confined.</summary>
    public bool SuppressVsync;
}
