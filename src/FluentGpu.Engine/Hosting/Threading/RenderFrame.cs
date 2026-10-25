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

    /// <summary>The restore hold rides the publication (never a side flag): this frame is to be recorded and submitted but NOT
    /// presented, because the restore it belongs to is still waiting for the images its first frame needs. The render thread decides
    /// per ADOPTED frame (and keeps its own latch, see <c>AppHost.DecideHold</c>), so a release can never race the publication it
    /// governs: the release is simply the first publication without this bit.</summary>
    public bool PresentHeld;

    /// <summary>E21: this frame will not be SHOWN (the main window still hidden behind its reveal gate, a freeze hold, a restore
    /// hold), so the render-thread compositor keeps every pending-start row (Enter, Stagger, Exit, a flow reveal) at t = 0 and
    /// starts its clock on the first shown frame (<c>RenderCompositorAnimations.HoldPendingStarts</c>). The UI scheduler held
    /// its own rows on the same verdict.</summary>
    public bool EntranceHeld;

    /// <summary>The frame-freeze directive this publication carries (E2). Like <see cref="PresentHeld"/> it rides the publication,
    /// never a side flag: the render side acts on each ADOPTED frame by this value, so a release can never race the frame it
    /// governs. A freeze directive replaces the restore hold's faithful-frame extension for this frame (the freeze release is
    /// exactly the frame that carries <see cref="FreezeKind.Dissolve"/> or <see cref="FreezeKind.Release"/>).</summary>
    public FreezeKind Freeze;

    /// <summary>The dissolve progress 0..1 of a <see cref="FreezeKind.Dissolve"/> frame: <c>(1-t)·snapshot + t·this frame</c>, on the
    /// UI's clock. The inline path (no render thread) presents it; a render thread samples its own clock instead (<see cref="DissolveMs"/>).</summary>
    public float DissolveT;

    /// <summary>The dissolve's duration (ms) and its identity (one per thaw) on a <see cref="FreezeKind.Dissolve"/> frame. The presenting
    /// render thread samples the dissolve at each present's predicted time, t = 0 on the first presented dissolve frame (the
    /// pending-start rule of every entrance), so a frame the UI could not produce in time neither stalls nor skips it.</summary>
    public float DissolveMs;
    public uint DissolveId;

    /// <summary>How many entries of the slot's video-intent block this publication carries (F070): the snapshot of the UI-owned
    /// <c>VideoSurfaceRegistry</c> taken at publish, read through <see cref="SceneFramePublisher.VideoIntents"/>. The render thread
    /// places video from THIS, never from the live registry, so the video moves with the frame whose hole it sits behind.</summary>
    public int VideoIntentCount;
}

/// <summary>What a publication asks of the frame freeze (E2). The UI decides per frame; the presenting side obeys.</summary>
public enum FreezeKind : byte
{
    /// <summary>No freeze: the ordinary path.</summary>
    None = 0,
    /// <summary>The OUTGOING frame: submit it, snapshot the back buffer (premultiplied, with alpha) and present it normally.</summary>
    Capture = 1,
    /// <summary>Frozen: record and submit (uploads, atlas and copies land) but present nothing; video releases are deferred.</summary>
    Hold = 2,
    /// <summary>Thawing: present the dissolve <c>(1-t)·snapshot + t·this frame</c> (a full present); video resumes.</summary>
    Dissolve = 3,
    /// <summary>The freeze ends with this frame: present it normally and release the snapshot (Dissolve(0), a cut, the end of a dissolve).</summary>
    Release = 4,
}
