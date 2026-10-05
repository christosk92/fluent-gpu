using FluentGpu.Rhi;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// POD header naming one generation-claimed publisher slot. Hosted rendering carries detached scene recording inputs
/// (<see cref="HasScene"/>); transport tests and inline consumers may carry finished command bytes instead.
/// Payload arrays and retained resource references belong to the slot, not this copied header. The consumer must claim
/// that exact generation before reading either header or payload and retains the claim across independent animation turns.
/// </summary>
public struct RenderFrame
{
    /// <summary>True when the slot carries scene recording inputs instead of a completed command stream.</summary>
    public bool HasScene;
    public long TargetEpoch;
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

    /// <summary>This frame is a resize SETTLE frame (<c>resized &amp;&amp; keepAlive</c>): arm the swapchain's settle hint
    /// (<see cref="ISwapchain.HintSettlePresent"/>) on the thread that presents THIS frame, just before its submit. It rides the
    /// publication like <see cref="SuppressVsync"/> instead of being a flag the UI thread pokes into render-owned state, so a
    /// render turn presenting an earlier publication can never consume it, and a frame the render side elides never leaves
    /// it standing for an unrelated later present.</summary>
    public bool SettlePresent;

    /// <summary>How many entries of the slot's video-intent block this publication carries (F070): the snapshot of the UI-owned
    /// <c>VideoSurfaceRegistry</c> taken at publish, read through <see cref="SceneFramePublisher.VideoIntents"/>. The render thread
    /// places video from THIS, never from the live registry, so the video moves with the frame whose hole it sits behind.</summary>
    public int VideoIntentCount;
}
