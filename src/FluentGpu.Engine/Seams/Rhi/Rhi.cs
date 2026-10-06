using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Rhi;

/// <summary>Per-frame context handed to the device at submit. POD.</summary>
// ImageClockMs = the image cache's crossfade clock for this frame (replay-time image reveals sample it).
// RepaintDamage = the REPAINT set (gpu-renderer.md §13): every region whose PIXELS may differ from the last presented
// frame — old∪new for moved nodes, prior∪current for paint/layout re-records, vacated extents for removals, the viewport
// for a scrolled content node — each padded by the AA floor + its effect halo. Empty + RepaintFullReason.None means
// NOTHING changed; a forced-full region names the cause (and invalidates every retained tile).
// PublishSequence = the monotonic seq SceneFramePublisher.Publish stamped on this frame (0 = never published, e.g. a
// direct SubmitDrawList). A consumer that sees a jump of more than one since the frame it last consumed missed logical
// frames; the publisher already unions the skipped frames' RepaintDamage forward, and this is the backstop that lets the
// consumer notice anyway.
// CarriedFromSeq = the OLDEST publish seq whose RepaintDamage is folded into this frame's region (== PublishSequence when
// nothing was dropped). It is what makes a publish-gap answerable: the question a consumer must ask is not "was the gap
// zero?" (DropOldest makes gaps normal under load, and the publisher's carry already covers them) but "was the gap's
// damage carried?", i.e. CarriedFromSeq <= lastConsumedSeq + 1.
public readonly record struct FrameInfo(Size2 SizePx, float Scale, ColorF Clear, float ImageClockMs = 0f,
    RepaintDamageRegion RepaintDamage = default, ulong PublishSequence = 0, ulong CarriedFromSeq = 0);

/// <summary>A coherent whole-command-list GPU execution measurement owned by one swapchain. <paramref name="Sequence"/>
/// is monotonic within that target; <paramref name="SubmitAge"/> is how many submissions to the SAME target have happened
/// since the measured submit (the double-buffered D3D path normally publishes at age 2); <paramref name="PublishedQpc"/>
/// is the CPU QPC instant at which fence retirement made the timestamp pair readable.</summary>
public readonly record struct GpuRenderSample(double ExecutionMs, ulong Sequence, ulong SubmitAge, long PublishedQpc)
{
    /// <summary>The target-local submit the sample measured (0 = unknown); the current submit is this plus <see cref="SubmitAge"/>.</summary>
    public ulong SubmitSequence { get; init; }
    /// <summary>The GPU's begin / end timestamps mapped onto QPC through the queue's clock calibration (0 = unknown).</summary>
    public long GpuStartQpc { get; init; }
    /// <inheritdoc cref="GpuStartQpc"/>
    public long GpuEndQpc { get; init; }
}

[Flags]
public enum RectSubmittedAreaFlags : byte
{
    None = 0,
    Rounded = 1,
    Stroked = 2,
    RoundedClip = 4,
    NonPlainKind = 8,
}

/// <summary>One large blended-rect descriptor from a submitted-area diagnostic snapshot. Local W/H are DIP;
/// <paramref name="AreaPx2"/> includes the affine determinant and DPI scale. Ordinal is among submitted rect instances,
/// not a scene-node/source identity.</summary>
public readonly record struct RectSubmittedAreaItem(
    int Ordinal, double AreaPx2, float EffectiveAlpha, float LocalW, float LocalH, RectSubmittedAreaFlags Flags);

/// <summary>Coherent target-local rect submitted-area snapshot. Areas are nominal transformed px², not coverage:
/// clipping and overlap are deliberately not removed. Sequence increments once per successful target submit.</summary>
public readonly record struct RectSubmittedAreaSample(
    ulong Sequence, int OpaqueInstances, int BlendedInstances, bool HasArea,
    double OpaquePx2, double BlendedPx2, int TopCount);

/// <summary><paramref name="DesktopAcrylic"/> = back this composited popup with a true desktop-sampling acrylic
/// (Windows.UI.Composition host backdrop) tinted by <paramref name="AcrylicTint"/> — the WinUI MenuFlyout material,
/// reached without the Windows App SDK. Ignored by backends that don't support it (they fall back to a plain swapchain).</summary>
public readonly record struct SwapchainDesc(NativeHandle PresentTarget, Size2 SizePx, bool Composited = false,
    bool DesktopAcrylic = false, ColorF AcrylicTint = default, float CornerRadiusPx = 0f);

/// <summary>
/// Graphics-first render hardware interface. Zero COM types cross this seam — generational handles + POD + spans only.
/// <see cref="SubmitDrawList"/> is the PRIMARY hot path: the leaf walks the POD opcode stream with concrete devirtualized
/// types. D3D12 is the reference backend; <c>Rhi.Headless</c> is the test backend; Metal slots in later behind this seam.
/// </summary>
public partial interface IGpuDevice : IDisposable
{
    string BackendName { get; }
    /// <summary>True when <see cref="CreateSwapchain"/> may be called for secondary popup targets and
    /// <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/> can render to
    /// those targets. Headless and D3D12 support this; future backends can opt in without changing the host.</summary>
    bool SupportsSecondarySwapchains => false;
    ISwapchain CreateSwapchain(in SwapchainDesc desc);

    /// <summary>How many completed presents the swapchain may queue before frame production blocks (DXGI
    /// SetMaximumFrameLatency). Pacing predicts the presented vblank as FrameQpc + (1 + MaxFrameLatency)·refresh
    /// (RefreshLattice.Build). Default 1 — the classic latency-1 contract; HeadlessGpuDevice keeps it so the
    /// deterministic gates keep PresentQpc = FrameQpc + 2·refresh. D3D12 starts at 1: its present-queue depth is a
    /// LATENCY decision chosen at runtime from measured GPU margin (<see cref="SetPresentQueueDepth"/>, 1..2), deliberately
    /// decoupled from its 3 CPU-side frame banks.</summary>
    int MaxFrameLatency => 1;

    /// <summary>Render thread: set the present-queue depth (DXGI maximum frame latency) for the primary swapchain —
    /// chosen by the host from measured GPU margin (<c>PresentQueueDepthPolicy</c>). Returns the depth now in force
    /// (a backend without a queue, or one that refuses, returns its unchanged <see cref="MaxFrameLatency"/>).</summary>
    int SetPresentQueueDepth(int depth) => MaxFrameLatency;

    /// <summary>The per-swapchain form of <see cref="SetPresentQueueDepth(int)"/>: set <paramref name="target"/>'s OWN present-queue
    /// depth (a detached pop-out's swapchain follows its own measured GPU margin, <c>PresentQueueDepthPolicy</c> per host) and
    /// never another target's: a child's samples must not retarget the primary, and the primary's must not retarget a child.
    /// The primary swapchain's form is <see cref="SetPresentQueueDepth(int)"/>. Returns the depth now in force for
    /// <paramref name="target"/> (a backend without a queue, a target it did not create, or one that refuses, returns its
    /// unchanged depth; default: <see cref="MaxFrameLatency"/>). Render thread only.</summary>
    int SetPresentQueueDepth(ISwapchain target, int depth) => MaxFrameLatency;

    /// <summary>Runtime toggle for the PASS-granular GPU timeline (<see cref="ISwapchain.CopyGpuPassTimeline"/>): timestamps
    /// only at pass boundaries, read back one submission later. Off by default; settable at any time from any thread (the
    /// render thread observes it at the next submit). A backend without timestamp queries ignores it.</summary>
    bool GpuPassTimingEnabled { get => false; set { } }

    /// <summary>Measurement knockouts (<see cref="GpuKnockouts"/>) — runtime-settable from a probe or the Diagnostics page,
    /// never an environment variable. Default None; a backend that cannot honour one ignores it.</summary>
    GpuKnockouts Knockouts { get => GpuKnockouts.None; set { } }

    /// <summary>Take the primary swapchain's present-slot credit (one Present spends it), waiting at most
    /// <paramref name="timeoutMs"/> (−1 = the backend's liveness bound). True when the credit is held (or the backend has no
    /// present queue); false when the slot did not open in time — no credit was taken, nothing to undo. Render-thread only.
    /// A backend that implements this MUST skip its internal pacing wait while a credit is held.
    /// <para>Called by the render loop BEFORE it picks which published frame to present, so the frame that reaches the
    /// glass is the freshest one that existed when the slot opened; a backend that waits inside submit instead ages the
    /// frame it already chose by the whole wait. A clock-paced turn passes a short grace (<c>SlotCatchUp.GraceMs</c>): a
    /// slot still busy after it means the previous present missed its vblank, and the loop skips that tick instead of
    /// queueing a frame that could only land late (see D3D12Device.TryTakePresentSlot) — so the bound must hold at
    /// millisecond precision (a plain OS wait timeout is only as fine as the timer resolution). Default true: the headless
    /// seam has no present queue, and a backend without a latency waitable keeps waiting inside submit.</para>
    /// <para>A NEGATIVE <paramref name="timeoutMs"/> is the liveness-bounded wait that proceeds (credit held) even when the slot
    /// never opened, so a lost device cannot wedge the loop: <c>-1</c> bounds it at the backend's default (1 s), any other
    /// <c>-N</c> at N ms (an unpaced turn's <c>max(2 x refresh, 34 ms)</c>, F208 - a minimized or cloaked primary whose
    /// presents never retire no longer costs a second per turn). A backend counts and reports the liveness timeouts
    /// (<see cref="SlotLivenessTimeouts"/>).</para></summary>
    bool TryTakePresentSlot(int timeoutMs) => true;

    /// <summary>Release the textures behind the tile surface slots the table freed on the IDLE path
    /// (<c>SliceTable.TrimFreeSlotsNow</c>) — the same retire-behind-the-fence release a composite turn's
    /// <c>CompositeFrame.TrimSurfaces</c> performs, for an app that is not compositing. Render thread, between turns. Default: none.</summary>
    void TrimTileSurfaces(ReadOnlySpan<int> slots) { }

    /// <summary>Periodic render-thread housekeeping between turns: drain the retired-resource queues the fence has passed and
    /// release idle, fully-rebuildable resources (free scratch surfaces, the stencil surface, staging banks) — all on wall
    /// clock, because an idle app runs no turns to age them. Returns the milliseconds until it wants to run again (-1 = nothing
    /// pending). Default: nothing to do.</summary>
    int TrimIdleResources(long nowMs) => -1;

    /// <summary>The hidden-window memory stage the host reached (render thread, or the UI thread in SingleThread mode, BETWEEN
    /// turns): <see cref="Hosting.HiddenStage.Shallow"/> drops every device resource no visible frame needs - the scratch / retained /
    /// group / blur surfaces, the stencil target of the primary swapchain, the free image-texture pools, the staging ring and the
    /// placed-heap warm pages - behind their fences, and keeps image textures from refilling the free pools while the stage holds;
    /// <see cref="Hosting.HiddenStage.Visible"/> lifts that. Idempotent. The tiles are released by the host first
    /// (<c>SliceTable.EvictAll</c> + <see cref="TrimTileSurfaces"/>). Default: nothing to release.</summary>
    /// <param name="stage">The stage to apply.</param>
    /// <param name="otherWindowVisible">Another window (a pop-out) shares this device and is on screen: skip the device-wide parts
    /// (scratch surfaces, blur pyramids, image pooling) and release only what is the hidden window's own.</param>
    void ReleaseHiddenResources(Hosting.HiddenStage stage, bool otherWindowVisible = false) { }

    /// <summary>True while a hidden-stage release still waits on a fence (retired surfaces or image textures not yet destroyed), so the
    /// host keeps its idle-trim pass armed until the backlog drains. Default false.</summary>
    bool HasHiddenReleaseBacklog => false;

    /// <summary>Liveness-bounded present-slot takes of the PRIMARY swapchain that timed out so far (CUMULATIVE; render thread writes,
    /// any thread reads): the take proceeded without the slot ever opening. A secondary swapchain's are
    /// <see cref="NonPrimaryLatencyTimeouts"/>. Surfaced as <c>slotTimeouts=</c> in the <c>[render.pace]</c> line.
    /// Default 0: the headless seam has no present queue.</summary>
    long SlotLivenessTimeouts => 0;

    /// <summary>Blocking latency waits a NON-primary swapchain's submit (a detached pop-out's, a popup's) ran out the 1 s bound for
    /// without its slot opening (CUMULATIVE; any thread reads). They run on the shared render thread, so each is a main-window
    /// stall attributable to that secondary window; <see cref="SlotLivenessTimeouts"/> counts the PRIMARY's only. The
    /// <c>[render.pace]</c> line's <c>timeoutTarget=primary|child</c> is the difference of the two (F235). Default 0.</summary>
    long NonPrimaryLatencyTimeouts => 0;

    /// <summary>The longest blocking latency wait of a non-primary swapchain since <see cref="ResetNonPrimaryLatencyWindow"/>, in
    /// milliseconds: the <c>childWaitMax=</c> of the <c>[render.pace]</c> line (the primary's own is <c>slotWaitMax=</c>). Default 0.</summary>
    double NonPrimaryLatencyWaitMaxMs => 0.0;

    /// <summary>Start a new <see cref="NonPrimaryLatencyWaitMaxMs"/> window (the pace report opens one every second). Render thread.</summary>
    void ResetNonPrimaryLatencyWindow() { }

    /// <summary>The per-swapchain form of <see cref="TryTakePresentSlot(int)"/>: take <paramref name="target"/>'s OWN
    /// present-slot credit (waiting at most <paramref name="timeoutMs"/>, 0 = a non-blocking probe). The shared render thread
    /// asks this for a detached pop-out's swapchain BEFORE it acquires that pop-out's frame, so it never blocks on a
    /// secondary window's vblank (and never on the main window's). Same contract otherwise: true when the credit is held (or
    /// the backend has no present queue); false when the slot did not open in time, nothing taken. Render-thread only.
    /// Default true: the headless seam has no present queue.</summary>
    bool TryTakePresentSlot(ISwapchain target, int timeoutMs) => true;

    /// <summary>Hand the device the render loop's park-request event (a manual-reset Win32 event handle; 0 = none, the
    /// default and what the host passes again before it closes the event). Every blocking wait the render thread makes for a
    /// present slot — <see cref="TryTakePresentSlot(int)"/>, the per-swapchain take and the wait inside a submit — then waits
    /// on {the slot's waitable, this event}, the waitable FIRST: a slot that is open still wins, and a park request alone ends
    /// the wait with NOTHING taken (the take returns false; a submit's wait leaves the credit un-held), so the UI's
    /// rendezvous never waits out a 1 s slot wait. The event is owned by the caller; the device only waits on it. Called
    /// before the render loop starts and after it was joined, never while it runs. Default no-op: the headless seam has no
    /// present queue to wait on.</summary>
    void SetSubmitAbortHandle(nint handle) { }

    /// <summary>UI thread: do the thread-affine part of creating <paramref name="desc"/>'s swapchain before the render thread
    /// creates the swapchain itself (a popup's create rides the render-thread mailbox). The D3D12 backend brings up the
    /// process-wide Windows.UI.Composition compositor here for a desktop-acrylic popup: it binds to the creating thread's
    /// DispatcherQueue, and only the UI thread pumps one. Default no-op.</summary>
    void PrepareSwapchainCreate(in SwapchainDesc desc) { }

    /// <summary>Best-effort local (device-dedicated) VRAM usage vs the OS-reported budget for this adapter, in bytes.
    /// Returns <see langword="false"/> when the backend cannot report it (the headless seam, and any real backend before
    /// its first sample) — callers must treat a false return as "unknown" and skip pressure-relief. The D3D12 backend
    /// fills these from the LOCAL memory segment it already polls (QueryVideoMemoryInfo). Used by the host's Weak-tier
    /// VRAM-pressure eviction (adreno-hang-fixes.md M5); default keeps every other backend unaffected.</summary>
    bool TryGetVramUsage(out long usedBytes, out long budgetBytes) { usedBytes = 0; budgetBytes = 0; return false; }

    /// <summary>The composited-video presenter (DirectComposition child visuals for externally-produced video / protected
    /// DRM surfaces), or <see langword="null"/> when this backend/target cannot composite video — the headless seam, or
    /// an opaque non-composited window. Default <see langword="null"/> keeps every non-D3D12 backend AND the headless
    /// test seam free of video, so the host's phase-11 video-surface drain is a no-op there and the zero-alloc gates are
    /// untouched by construction. The D3D12 backend returns its render-thread-confined <c>DCompVideoPresenter</c> (only
    /// while the primary swapchain is composited).</summary>
    FluentGpu.Pal.IVideoPresenter? VideoPresenter => null;

    /// <summary>The composited-video presenter bound to a SPECIFIC swapchain's DirectComposition root — the per-window
    /// form of <see cref="VideoPresenter"/> (which targets the primary swapchain). A detached/secondary video window
    /// passes its own swapchain here so its video child visuals attach under ITS DComp root, not the primary's. Returns
    /// <see langword="null"/> when the target is not composited / the backend cannot composite video. Default routes to
    /// the primary <see cref="VideoPresenter"/> so single-window backends are unaffected.</summary>
    FluentGpu.Pal.IVideoPresenter? GetVideoPresenter(ISwapchain swapchain) => VideoPresenter;

    /// <summary>The ONE device-level composition flush of a render turn (F080): every presenter's queued mutations
    /// (<see cref="FluentGpu.Pal.IVideoPresenter.ApplyPending"/>) are made visible by a single commit on the shared composition
    /// device, after the parent and every detached child drained. No-op when nothing was applied since the last commit, and on a
    /// backend without a composition device (the default). Render thread only.</summary>
    void CommitVideoComposition() { }

    /// <summary>Record + batch + submit the per-frame DrawList. <paramref name="drawList"/> is the POD command stream.</summary>
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx);

    /// <summary>Render-thread seam (Step 0): the host calls this once it has spawned the render thread so the backend can
    /// arm its submit/present thread-confinement assert (a stray UI-thread submit/present then throws under FGGUARD).
    /// No-op by default (headless / single-thread backends have nothing to confine).</summary>
    void MarkRenderConfined() { }

    /// <summary>Render-thread seam (Step 1, ASYNC only): the host calls this after wiring the image-upload queue so the
    /// backend arms confinement on its image texture store (Stage/Free/FlushUploads then throw under FGGUARD off the
    /// render thread). Separate from <see cref="MarkRenderConfined"/> because force-sync still stages on the UI thread
    /// (no overlap), so its image store must NOT be confined. No-op by default.</summary>
    void MarkImageUploadsRenderConfined() { }

    /// <summary>Render-thread seam (Step 1, ASYNC only): drain the UI→render image-upload queue on the RENDER thread,
    /// immediately before the frame's <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo)"/>
    /// opens its command list — staging uploads / freeing evictions there keeps the texture store single-toucher. An
    /// upload's transferred buffer is returned to <c>ArrayPool&lt;byte&gt;.Shared</c> after staging; a rejected upload is
    /// posted back via <see cref="ImageUploadQueue.PostReject"/>. No-op by default (headless has no queue wired).</summary>
    void DrainImageJobs(Hosting.Threading.ImageUploadQueue queue) { }

    /// <summary>Install the persistent derived-image bake handoff. The backend drains jobs at the top of a submit and
    /// posts completions after registering the output as an ordinary resident image. Headless backends may complete
    /// jobs semantically without rasterizing pixels.</summary>
    void SetBakedBlurQueue(Hosting.Threading.BakedBlurQueue queue) { }

    // ── Device-lost recovery (Step 4, ASYNC only; design/subsystems/threading-render-seam.md §9) ──
    /// <summary>Arm async device-lost SIGNALING: on a device-removed/reset/hung HRESULT the backend records the reason +
    /// bails the frame instead of throwing on the render thread (an unobserved background exception = process death), and
    /// its fence waits become bounded (no INFINITE hang on a lost device). Called by the host under async. No-op default.</summary>
    void EnableAsyncDeviceLostSignaling() { }

    /// <summary>The recorded device-lost reason (0 = healthy). The host polls this each UI frame; non-zero drives the
    /// recover handshake. Default 0 (headless / single-thread never signals — they keep the throw-on-loss path).</summary>
    int PollDeviceLost() => 0;

    /// <summary>Render thread (Step 4): rebuild the lost device — dispose every ComPtr WITHOUT waiting on the dead fence,
    /// then recreate device/queue/allocators/command-list/fence + all pipelines + every swapchain, zero the fence
    /// bookkeeping, and clear the lost-reason. Invoked from the render loop's recover gate under the UI's park. No-op default.</summary>
    void RecoverDevice() { }

    /// <summary>Render thread (Step 4): after a submit/present threw, was it a device removal? If so, record the reason
    /// (so the UI recover gate fires) and return true so the caller can SWALLOW the exception (keeping the render thread
    /// alive). Returns false for a non-device-loss throw (a genuine bug — must not be masked). Default false.</summary>
    bool NoteIfDeviceLost() => false;

    /// <summary>Diagnostic hook invoked after device loss is confirmed and before <see cref="RecoverDevice"/> releases
    /// backend state. Backends should write DRED/breadcrumb/native-resource details through <paramref name="write"/>.
    /// Default no-op for headless and non-D3D backends.</summary>
    void DumpDeviceLostDiagnostics(Action<string> write) { }

    /// <summary>Force a controlled device removal without TDR-ing the whole desktop. Used by the
    /// --fg device-lost=N test hook to exercise the async recovery rendezvous, and by a runtime adapter switch
    /// (set the backend's preferred adapter, then call this — recovery re-creates the device, honoring the
    /// preference). No-op default (headless / no injection support).</summary>
    void InjectDeviceLost() { }

    /// <summary>UI recover gate: has the app requested a live GPU-adapter switch since the last poll? Test-and-clears
    /// the request. When true the host drives the SAME rendezvous a device loss takes — call
    /// <see cref="InjectDeviceLost"/> and enter recovery, which re-creates the device on the newly-preferred adapter.
    /// Keeps the engine seam TerraFX-free: the D3D12 backend overrides this to consume its GpuAdapterInfo flag; every
    /// other backend keeps the false default (no live-switch support).</summary>
    bool ConsumeAdapterSwitchRequest() => false;

    /// <summary>Always-on P0 counter (cumulative, render-thread writes / UI-thread reads as a rough gauge): image-upload
    /// drain turns (<see cref="DrainImageJobs"/>) that hit the backend's per-turn pixel-byte budget and carried a job to
    /// the next turn. The host mirrors it onto <c>FrameStats.DeferredImageUploads</c>; a consumer differences frames. A
    /// sustained climb during a cover-heavy scroll means landed covers arrive faster than one present turn stages them.
    /// Default 0: the headless seam and any backend without a budgeted drain. Typed <c>int</c> to match the D3D12
    /// backend's property — an interface member of another width would silently keep this default.</summary>
    int DeferredImageUploads => 0;

    /// <summary>Cumulative pixel bytes of the jobs <see cref="DeferredImageUploads"/> counted. Default 0.</summary>
    long DeferredImageUploadBytes => 0;

    /// <summary>True when decoded image pixels are staged but not yet copied to their resident GPU texture. The host
    /// must NOT elide that submit, or the texture stays empty and the image renders white. Default false (a
    /// headless/synchronous backend has nothing pending). Deliberately excludes the retire backlog (evicted resources
    /// waiting on their fence) — that is fence-only maintenance, reclaimed for free on an elided frame via
    /// <see cref="ReclaimCompletedUploads"/> instead of forcing a submit.</summary>
    bool HasPendingUploads => false;

    /// <summary>True when <paramref name="imageId"/> has a texture a draw samples RIGHT NOW (staged, and any side-queue copy
    /// done): a frame recorded at this point draws the picture rather than a placeholder for it. The held restore's final gate
    /// asks it on the render thread, after the frame was recorded. Default true (a backend with no deferred residency).</summary>
    bool IsImageResident(int imageId) => true;

    /// <summary>What one image texture of the square <paramref name="bucket"/> (64 / 128 / 256 / 512 px) really commits on this
    /// device, measured from the driver's own allocation requirement the first time such a texture was created (0 = not measured
    /// yet or not supported). The image cache charges its budget in these units instead of the 64 KiB-aligned formula, which the
    /// Adreno beats by 25 % for 256 x 256 (320 KiB against 256 KiB). Any thread; default 0.</summary>
    long ImageCommittedBytes(int bucket) => 0;

    /// <summary>A Deep restore's held frames are not presented, so the missed-vblank reason for the per-turn upload cap does not
    /// apply to them: while true the render-thread image drain stages every queued upload instead of
    /// <c>UploadBytesPerTurn</c> of them, and the held first frame lands in the fewest turns. Render thread; default no-op.</summary>
    void SetUploadCapLifted(bool lifted) { }

    /// <summary>Fence-only maintenance for an elided frame: releases image resources whose retire fence completed,
    /// without opening a command list or owing a present. Call this instead of a full submit when the frame would
    /// otherwise be skipped/elided, so a backlog of evicted textures doesn't sit resident forever on a quiet UI.
    /// Default no-op (headless / backends with no deferred-retire resource pool).</summary>
    void ReclaimCompletedUploads() { }

    /// <summary>Record + batch + submit to a specific swapchain target (windowed popup HWNDs). Backends without
    /// secondary-swapchain support fall back to the primary target via the legacy overload.</summary>
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
        => SubmitDrawList(drawList, sortKeys, in ctx);

    /// <summary>Hand decoded PREMULTIPLIED BGRA8 pixels for <paramref name="imageId"/> to the backend (the
    /// media-pipeline §4.1 texture upload). The backend create-or-replaces a resident texture (or atlas page) keyed by
    /// id and samples it from the <c>DrawImage</c> opcode. <paramref name="pbgra8"/> is valid only for this call —
    /// the backend copies it into its texture-staging ring; it is never retained. Rows may not be 256-aligned; the
    /// backend pads. Called once per decode completion, before <see cref="SubmitDrawList"/>.</summary>
    void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h);

    /// <summary>Admission-aware image upload. Existing backends remain source-compatible through this default, which
    /// delegates to <see cref="UploadImage"/> and assumes success; bounded backends override it so the cache never marks
    /// a rejected texture Ready.</summary>
    ImageUploadResult TryUploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        UploadImage(imageId, pbgra8, w, h);
        return ImageUploadResult.Accepted;
    }

    /// <summary>The residency manager evicted <paramref name="imageId"/> — release its GPU texture (deferred behind the
    /// frame fence so an in-flight frame can't read freed memory). No-op if not resident.</summary>
    void EvictImage(int imageId) { }

}

/// <summary>Geometry + motion parameters for a desktop-acrylic windowed popup's composition chrome. All px, relative to
/// the (shadow-inset-inflated) popup window. <paramref name="ContentRectPx"/> is the rounded menu plate inside the
/// window's shadow margins; the acrylic is rounded to it and the open slide = <c>ContentRectPx.H * ClosedRatio</c>.
/// <paramref name="OpensUp"/> = menu opens upward (anchored at its bottom). <paramref name="ClosedRatio"/> follows
/// WinUI's MenuPopupThemeTransition (0.5 root menu, 0.67 cascaded submenu).</summary>
public readonly record struct PopupChromeMetrics(
    RectF ContentRectPx, bool OpensUp, float ClosedRatio, float CornerRadiusPx, float BorderPx);

public interface ISwapchain : IDisposable
{
    Size2 SizePx { get; }
    void Resize(Size2 px);
    void Present();

    /// <summary>Present WITHOUT blocking the calling thread on this target's present queue (DXGI_PRESENT_DO_NOT_WAIT): the
    /// non-blocking secondary present (F085) a detached pop-out's frame takes on the render thread it shares with the main
    /// window. True when the frame is queued, stood down or otherwise handled exactly as <see cref="Present"/> would have
    /// (including a covered window's stand-down). False when the backend REFUSED it because the queue was still full
    /// (DXGI_ERROR_WAS_STILL_DRAWING): nothing was queued, so the present-slot credit the frame would have spent is still
    /// held, the drawn back buffer is untouched, and the caller owes the frame and may re-present it on a later turn. The
    /// default is the plain blocking <see cref="Present"/> (a backend with no queue to refuse from).</summary>
    bool PresentNoWait()
    {
        Present();
        return true;
    }

    /// <summary>Read the most recently retired whole-frame GPU execution sample for THIS target. Returns false when the
    /// backend cannot measure it or no sample for this swapchain has retired yet. Target ownership is load-bearing: a
    /// popup/child submission must never update the main host's governor (and vice versa).</summary>
    bool TryGetGpuRenderSample(out GpuRenderSample sample)
    {
        sample = default;
        return false;
    }

    /// <summary>Copy the most recently RETIRED frame's pass-granular GPU timeline for THIS target (coherent; only while
    /// <see cref="IGpuDevice.GpuPassTimingEnabled"/> is on). Returns the number of passes copied into
    /// <paramref name="dst"/> (0 when disabled / none retired yet / unsupported).</summary>
    int CopyGpuPassTimeline(Span<GpuPassTiming> dst, out GpuPassFrameSummary summary)
    {
        summary = default;
        return 0;
    }

    /// <summary>The always-on device counters of this target's most recent successful submit (coherent). False when
    /// unsupported or before the first submit.</summary>
    bool TryGetFrameCounters(out GpuFrameCounters counters)
    {
        counters = default;
        return false;
    }

    /// <summary>Copy one coherent target-local submitted-rect snapshot: opaque/blended instance counts are always
    /// available on supporting backends; <see cref="RectSubmittedAreaSample.HasArea"/> gates the optional
    /// <c>--fg render</c> areas and fixed top-N descriptors. Returns false when unsupported or before the target's
    /// first submit. Implementations must not expose mutable render-thread counters through this seam.</summary>
    bool TryCopyRectSubmittedAreaSample(Span<RectSubmittedAreaItem> blendedTop, out RectSubmittedAreaSample sample)
    {
        sample = default;
        return false;
    }

    /// <summary>True once this target has PRESENTED at least one content frame — i.e. its front buffer / composition
    /// surface actually holds pixels this engine drew. A present is not guaranteed to happen just because one was
    /// requested: the Windows backend stands down for a covered/cloaked/hidden present target
    /// (<c>D3D12Device.Present</c>), so "we called Present" is not evidence of painted content.
    /// <para>Load-bearing for the ATOMIC POPUP REVEAL: a windowed popup's HWND is created hidden and revealed only
    /// once its swapchain reports true here, so a popup can never become visible as its frosted composition chrome
    /// with an empty content surface. Backends that do not track it report true (today's unconditional reveal).</para></summary>
    bool HasPresentedContent => true;

    /// <summary>Configure the windowed popup's composition chrome (rounded acrylic content rect + outer shadow) for the
    /// current placement. Called on each placement before show. Default no-op: only a backdrop-backed backend honors it.</summary>
    void ConfigurePopupChrome(in PopupChromeMetrics m) { }

    /// <summary>Play the open motion: the whole composition root (acrylic + content + shadow) slides from the anchor edge
    /// to rest over 250ms cubic-bezier(0,0,0,1), no opacity fade — WinUI MenuPopupThemeTransition. Uses the configured
    /// metrics. Idempotent — runs once per open.</summary>
    void AnimatePopupOpen() { }

    /// <summary>Play the close motion: fade the WHOLE composition root (so the acrylic fades too, not just the engine
    /// content) opacity 1→0 over 83ms. The host keeps the window alive until <see cref="PopupAnimating"/> clears.</summary>
    void AnimatePopupClose() { }

    /// <summary>True while this popup's open/close motion is mid-flight. The host ORs this into <c>WakeReasons.PopupAnim</c>
    /// so the frame loop keeps presenting the popup until the composition animation commits + settles (and, for close,
    /// defers disposal until it clears).</summary>
    bool PopupAnimating => false;

    // ── Per-target diagnostics + present pacing (detached-window-render-isolation-implementation.md §3.3) ──
    // Moved off IGpuDevice: each was a device-wide LAST-WRITER field that a secondary target's submit/present could
    // silently overwrite on behalf of the primary (INCIDENT 2026-09 §1.6 item 4). Target-scoped by construction here.

    /// <summary>True when the last submitted frame rendered text unfaithfully — a glyph-atlas overflow deferred its
    /// cache flush to the next frame, so some glyphs drew BLANK this frame for THIS target. The host must NOT
    /// skip-submit and must NOT treat this frame as a valid partial-repaint base while it is true: it owes exactly one
    /// more full frame so the backend can re-record with the fresh atlas generation. Default false
    /// (headless/synchronous backends never defer a reset).</summary>
    bool TextRepaintPending => false;

    /// <summary>True when the most recent <see cref="Present"/> of THIS target stood down (cloaked / OCCLUDED probe
    /// still occluded) without a real present. The host treats this like skip-submit for the sync-path pacing floor.</summary>
    bool LastPresentStoodDown => false;

    /// <summary>Make the NEXT <see cref="Present"/> / <see cref="PresentNoWait"/> of this target a HELD present: the frame is
    /// already fully recorded and submitted (so uploads, the glyph atlas and every texture copy land), but nothing is flipped, so
    /// the window keeps showing its previous frame. Used by the restore hold. One-shot: consumed by that present. A held present
    /// is NOT a stand-down: <see cref="LastPresentStoodDown"/> and <see cref="IsOccluded"/> stay false (nothing about the window
    /// is covered or hidden - a stood-down signal would freeze motion, flap the app-wide occlusion flag and stop visualizers).
    /// Render thread. Default no-op (a backend that cannot hold presents the frame).</summary>
    void HoldNextPresent() { }

    /// <summary>True when the most recent present of THIS target was held (<see cref="HoldNextPresent"/>).</summary>
    bool LastPresentHeld => false;

    /// <summary>True while THIS target is not being shown: the backend's DXGI occlusion latch is set (DXGI_STATUS_OCCLUDED —
    /// a fully covered HWND swapchain; NOT reliably reported for composition swapchains) OR its last present stood down
    /// (minimized / cloaked / hidden). A pure read for the host's per-frame publication (<c>InputHooks.WindowOccluded</c>).
    /// Window deactivation (alt-tab) is NOT folded in. Default false (synchronous backends).</summary>
    bool IsOccluded => false;

    /// <summary>Diagnostic: the OS-attested present/compositor statistics sampled at THIS target's last present, or
    /// <c>default</c> on a backend that has none (headless — the struct's <c>Valid</c> bit reads false, which every
    /// consumer must treat as NOT MEASURED rather than as zeroes). ALWAYS-ON: two OS calls per present and one per
    /// second respectively, no queries, no allocation — this is the only vblank-attested cadence truth available, and
    /// the in-app cadence metrics are computed against its refresh period rather than a nominal
    /// <c>GetDeviceCaps(VREFRESH)</c> value. See <see cref="PresentStats"/>.</summary>
    PresentStats LastPresentStats => default;

    /// <summary>Diagnostic: wall-time (ms) spent blocked on THIS target's frame-retirement fence inside the most recent
    /// <see cref="IGpuDevice.SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/>. Queue/
    /// back-buffer retirement, not GPU execution time. The host folds it into <c>FrameStats.FenceWaitMs</c>. Default 0
    /// for backends that do not block there.</summary>
    double LastFenceWaitMs => 0;

    /// <summary>Diagnostic: wall-time (ms) spent blocked on THIS target's present-latency waitable inside the most
    /// recent submit — compositor pacing, split out from <see cref="LastFenceWaitMs"/> so a child's latency wait can
    /// no longer masquerade as the primary's (INCIDENT 2026-09 §1.6 item 4). Default 0.</summary>
    double LastLatencyWaitMs => 0;

    /// <summary>Diagnostic (F244): the two waits THIS target's most recent submit paid inside itself, split so a slow present turn
    /// can name the call that blocked — <paramref name="latencyMs"/>, a frame-latency waitable wait the submit made because the
    /// render loop had not already taken the credit (0 when it held it), and <paramref name="bufferFenceMs"/>, the back-buffer /
    /// ring-slot frame-fence wait (the GPU still owning the buffer). Both 0 for a backend that does not block there (the default,
    /// and a target that has not submitted yet). Render-thread read, right after the submit.</summary>
    void GetLastSubmitWaits(out double latencyMs, out double bufferFenceMs)
    {
        latencyMs = 0;
        bufferFenceMs = 0;
    }

    /// <summary>OS-attested present statistics for THIS target, CUMULATIVE (a reader diffs two samples), from the
    /// engine's <c>PresentStatisticsLedger</c> over the backend's PAIRED frame-statistics counters: presents that reached
    /// a vblank, presents superseded before display (ΔPresentCount beyond ΔPresentRefreshCount — the silent drop a submit
    /// stamp cannot see), and vblanks that showed a stale frame although its successor had been submitted in time (idle
    /// vblanks and producer-skipped ticks are not repeats — the render thread's missed-tick counter owns the latter).
    /// Render-written, UI-read gauges; 0 for backends without frame statistics.</summary>
    long PresentsDisplayed => 0;
    /// <inheritdoc cref="PresentsDisplayed"/>
    long PresentsDropped => 0;
    /// <inheritdoc cref="PresentsDisplayed"/>
    long VblanksRepeated => 0;

    /// <summary>Suppress THIS target's frame-latency throttle wait at the start of the NEXT submit (self-resetting).
    /// The host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size loop, where
    /// the WndProc thread would otherwise block up to a vblank on the latency waitable — injecting the drag-start/
    /// live-resize hitch. Default no-op: only a backend with a present-latency throttle (D3D12) honors it.</summary>
    void SuppressLatencyWaitOnce() { }

    /// <summary>Present THIS target's NEXT frame at SyncInterval 0 instead of the steady-state vsync interval (self-
    /// resetting). The host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size
    /// loop: on a composited flip swapchain interval-0 is a cheap, tear-free hand-off (DWM still composites at
    /// vblank) so the WndProc thread isn't blocked up to a vblank in Present — the live-resize/move hitch the
    /// latency-wait skip alone doesn't remove. Default no-op: only a backend that presents to a real swapchain
    /// (D3D12) honors it.</summary>
    void SuppressVsyncOnce() { }

    /// <summary>Hint the backend to sync DWM composition once after THIS target's next present (self-resetting). The
    /// host calls this, on the thread that presents the frame, for a modal-loop SETTLE frame (<c>resized &amp;&amp;
    /// keepAlive</c>; <c>RenderFrame.SettlePresent</c>) so Mica/backdrop snaps with the final client size. The hint only ARMS
    /// the sync: the present itself never blocks on it, because the video placement for the frame must be committed first
    /// (see <see cref="CompleteSettlePresent"/>). Default no-op.</summary>
    void HintSettlePresent() { }

    /// <summary>Arm, for THIS target's next present only (self-resetting), the geometry-motion present (F070 Stage B): the frame
    /// being presented moves a video surface that is already on screen, so the present first waits (bounded by about one refresh) for
    /// the frame's own GPU work to retire, and the host commits the new video placement the instant the present returns. The new hole
    /// and the new video rect then become eligible for the SAME DWM composition instead of landing a queue-depth apart. A frame whose
    /// GPU work runs longer than the bound presents anyway (one skewed frame, counted). Armed ONLY for a turn that moves video
    /// geometry, never on steady playback or idle. Default no-op: a backend with no present queue to skew against.</summary>
    void HintGeometryMotionPresent() { }

    /// <summary>Run (or drop) the sync <see cref="HintSettlePresent"/> armed. The host calls this AFTER it committed the
    /// frame's video placement, so the hole and the video geometry reach DWM in the same composition: a blocking flush
    /// between the present and that commit would leave a composed frame with the new hole over the old video rect.
    /// <paramref name="blockUntilComposed"/> is true only for an inline (UI-thread) present, where blocking one vblank costs
    /// no other target anything. On the shared render thread it is false: the thread already waits on the compositor tick at
    /// the top of its next turn, and a DWM flush there stalls every other target it presents (a pop-out) for up to a
    /// refresh. Either way the hint is consumed. Default no-op.</summary>
    void CompleteSettlePresent(bool blockUntilComposed) { }
}
