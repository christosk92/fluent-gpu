using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Rhi;

/// <summary>Per-frame context handed to the device at submit. POD.</summary>
// Damage = the union (device/DIP px) of nodes whose transform moved this frame — the region-aware invalidation set for
// the in-app acrylic backdrop cache (default empty ⇒ nothing moved ⇒ reuse every cached blur). See AcrylicBackdropMath.
// FrameEpoch = a nonzero monotonic frame counter (0 = none) matched against PushLayerCmd.DamageEpoch: a cached acrylic
// layer whose baked epoch equals FrameEpoch uses its own EXTERNAL damage rect (own-subtree carve-out, §2.3/E9); a stale
// (span-copied) or unpatched (popup/uncached) layer mismatches and falls back to the whole-frame Damage union.
// ScrollHold = this PUBLISHED frame fell inside AppHost's user-scroll hold window (any user scroll this frame + the
// ~0.12s SelfBlurHold tail — the same latch that drives the self-blur groups' holdBlur). Frame-global by nature, and
// decided on the UI thread as the frame is published, so the render thread reads a flag that describes THIS frame's
// content rather than the UI thread's current instant. The acrylic retained-backdrop cache uses it to rate-limit the
// re-blur of a layer that already HAS a snapshot (§2.3/E10, AcrylicScrollHold.ShouldRefresh); a layer with no retained
// snapshot always blurs immediately, so the flag can never surface a fallback/garbage backdrop.
// RepaintDamage = the REPAINT set (gpu-renderer.md §13.1): every region whose PIXELS may differ from the last presented
// frame — old∪new for moved nodes, prior∪current for paint/layout re-records, vacated extents for removals, the viewport
// for a scrolled content node — each padded by the AA floor + its effect halo. Empty + RepaintFullReason.None means
// NOTHING changed; a forced-full region names the cause. It is DELIBERATELY not the same set as Damage above (which is
// the acrylic blur-cache union: transform-moved nodes only, scroll content and paint-only changes excluded) — the two
// answer different questions and must never be substituted for one another.
// PublishSequence = the monotonic seq SceneFramePublisher.Publish stamped on this frame (0 = never published, e.g. a
// direct SubmitDrawList). A consumer that sees a jump of more than one since the frame it last consumed missed logical
// frames; the publisher already unions the skipped frames' RepaintDamage forward, and this is the backstop that lets the
// consumer notice anyway.
// CarriedFromSeq = the OLDEST publish seq whose RepaintDamage is folded into this frame's region (== PublishSequence when
// nothing was dropped). It is what makes a publish-gap answerable: the question a consumer must ask is not "was the gap
// zero?" (DropOldest makes gaps normal under load, and the publisher's carry already covers them) but "was the gap's
// damage carried?", i.e. CarriedFromSeq <= lastConsumedSeq + 1. Treating a gap itself as a correctness event turns every
// dropped frame into a full repaint exactly when partial repaint matters most.
// DrawListHash = a content fingerprint of the command stream + sort keys this frame publishes (0 = not stamped). The
// backend's retained canvas remembers the hash it was last painted from, so a frame that claims "nothing changed" can be
// CHECKED rather than trusted: a mismatch means a damage source is missing, and one named full frame beats a permanent
// ghost. Reuses the host's existing skip-submit hash — never a second walk of the stream.
public readonly record struct FrameInfo(Size2 SizePx, float Scale, ColorF Clear, RectF Damage = default, float ImageClockMs = 0f, ulong FrameEpoch = 0, bool ScrollHold = false,
    RepaintDamageRegion RepaintDamage = default, ulong PublishSequence = 0, ulong CarriedFromSeq = 0, ulong DrawListHash = 0);

/// <summary>A coherent whole-command-list GPU execution measurement owned by one swapchain. <paramref name="Sequence"/>
/// is monotonic within that target; <paramref name="SubmitAge"/> is how many submissions to the SAME target have happened
/// since the measured submit (the double-buffered D3D path normally publishes at age 2); <paramref name="PublishedQpc"/>
/// is the CPU QPC instant at which fence retirement made the timestamp pair readable.</summary>
public readonly record struct GpuRenderSample(double ExecutionMs, ulong Sequence, ulong SubmitAge, long PublishedQpc);

/// <summary>Coherent optional FG_GPU_TIMING whole/scene/category timeline owned by one swapchain target.</summary>
public readonly record struct GpuProfileSample(ulong Sequence, double WholeMs, double SceneMs,
    double FillMs, double ShadowMs, double ImageMs, double GlyphMs, double CompositeMs);

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
public interface IGpuDevice : IDisposable
{
    string BackendName { get; }
    /// <summary>True when <see cref="CreateSwapchain"/> may be called for secondary popup targets and
    /// <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/> can render to
    /// those targets. Headless and D3D12 support this; future backends can opt in without changing the host.</summary>
    bool SupportsSecondarySwapchains => false;
    ISwapchain CreateSwapchain(in SwapchainDesc desc);

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

    /// <summary>Test hook (FG_FORCE_DEVICE_LOST): force a controlled device removal to exercise the async recovery
    /// rendezvous on real hardware, without TDR-ing the whole desktop. No-op default (headless / no injection support).</summary>
    void InjectDeviceLost() { }

    /// <summary>Diagnostic: wall-time (ms) spent blocked on the frame-retirement fence plus the present-latency waitable
    /// inside the most recent <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo)"/>. This is
    /// queue/back-buffer retirement and compositor pacing, not GPU execution time. The host folds it into
    /// <c>FrameStats.FenceWaitMs</c>. Default 0 for backends that do not block there.</summary>
    double LastFenceWaitMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): whole-command-list elapsed timestamp span paired with the detailed scene
    /// and category values below. This remains distinct from the target-local always-on
    /// <see cref="ISwapchain.TryGetGpuRenderSample"/> sample. 0 when off/unsupported.</summary>
    double LastGpuProfileMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): the SCENE-EXECUTION portion (clear + draw-list
    /// playback + layer composites), excluding image uploads and baked-blur. When this ≈ the whole and ≳ the refresh budget,
    /// the maximize lock is content fill/overdraw (not uploads/blur). 0 when off. Host folds into <c>FrameStats.GpuSceneMs</c>.</summary>
    double LastGpuSceneMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): of <see cref="LastGpuSceneMs"/>, the rect/solid-FILL portion (opaque/blended
    /// rects, arcs, polylines, gradients — shadows split out into <see cref="LastGpuShadowMs"/>). Isolates overdraw fill
    /// cost from image/text/composite. 0 when off/unsupported. Host folds into <c>FrameStats.GpuFillMs</c>.</summary>
    double LastGpuFillMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): of <see cref="LastGpuSceneMs"/>, the drop-SHADOW portion. Split out of
    /// <see cref="LastGpuFillMs"/> because a shadow is a large always-blended SDF quad whose cost tracks shadow COUNT and
    /// AREA, not the plate fills it batches beside. 0 when off. Folds into <c>FrameStats.GpuShadowMs</c>.</summary>
    double LastGpuShadowMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): of <see cref="LastGpuSceneMs"/>, the IMAGE-draw portion. 0 when off. Folds into <c>FrameStats.GpuImageMs</c>.</summary>
    double LastGpuImageMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): of <see cref="LastGpuSceneMs"/>, the GLYPH/text portion. 0 when off. Folds into <c>FrameStats.GpuGlyphMs</c>.</summary>
    double LastGpuGlyphMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): of <see cref="LastGpuSceneMs"/>, the layer/acrylic COMPOSITE portion. 0 when off. Folds into <c>FrameStats.GpuCompositeMs</c>.</summary>
    double LastGpuCompositeMs => 0;

    /// <summary>Diagnostic (FG_GPU_TIMING=1): true when the detailed scene/category timestamp block was resolved on the
    /// most recent submit. This is deliberately separate from the always-on whole-frame sample above.</summary>
    bool GpuTimingSampleFresh => false;

    /// <summary>True when the most recent <see cref="ISwapchain.Present"/> stood down (cloaked / OCCLUDED probe still
    /// occluded) without a real present. The host treats this like skip-submit for the sync-path pacing floor.</summary>
    bool LastPresentStoodDown => false;

    /// <summary>Diagnostic: the OS-attested present/compositor statistics sampled at the last present, or
    /// <c>default</c> on a backend that has none (headless — the struct's <c>Valid</c> bit reads false, which every
    /// consumer must treat as NOT MEASURED rather than as zeroes). ALWAYS-ON: two OS calls per present and one per
    /// second respectively, no queries, no allocation — this is the only vblank-attested cadence truth available, and
    /// the in-app cadence metrics are computed against its refresh period rather than a nominal
    /// <c>GetDeviceCaps(VREFRESH)</c> value. See <see cref="PresentStats"/>.</summary>
    PresentStats LastPresentStats => default;

    /// <summary>True when decoded image pixels are staged but not yet copied to their resident GPU texture, or when
    /// transient upload resources are awaiting fence-gated release. The host must NOT elide that submit, or the texture
    /// stays empty and deferred upload memory can remain resident until unrelated UI work happens. Default false (a
    /// headless/synchronous backend has nothing pending).</summary>
    bool HasPendingUploads => false;

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

    /// <summary>Suppress the frame-latency throttle wait at the start of the NEXT <see cref="SubmitDrawList"/> (self-
    /// resetting). The host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size
    /// loop, where the WndProc thread would otherwise block up to a vblank on the latency waitable — injecting the
    /// drag-start/live-resize hitch. Default no-op: only a backend with a present-latency throttle (D3D12) honors it.</summary>
    void SuppressLatencyWaitOnce() { }

    /// <summary>Present the NEXT frame at SyncInterval 0 instead of the steady-state vsync interval (self-resetting). The
    /// host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size loop: on a composited
    /// flip swapchain interval-0 is a cheap, tear-free hand-off (DWM still composites at vblank) so the WndProc thread isn't
    /// blocked up to a vblank in Present — the live-resize/move hitch the latency-wait skip alone doesn't remove. Default
    /// no-op: only a backend that presents to a real swapchain (D3D12) honors it.</summary>
    void SuppressVsyncOnce() { }

    /// <summary>Hint the backend to sync DWM composition once after the next present (self-resetting). The host calls
    /// this on a modal-loop SETTLE frame (<c>resized &amp;&amp; keepAlive</c>) so Mica/backdrop snaps with the final
    /// client size. Default no-op.</summary>
    void HintSettlePresent() { }
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
    /// <summary>True when a SyncInterval-0 present is still tear-free because the swapchain is handed to a desktop
    /// compositor (for example DirectComposition). The host uses this to scope interactive present experiments; opaque
    /// HWND swapchains and backends that do not opt in stay on their ordinary vsync path.</summary>
    bool SupportsCompositedIntervalZero => false;

    /// <summary>Read the most recently retired whole-frame GPU execution sample for THIS target. Returns false when the
    /// backend cannot measure it or no sample for this swapchain has retired yet. Target ownership is load-bearing: a
    /// popup/child submission must never update the main host's governor (and vice versa).</summary>
    bool TryGetGpuRenderSample(out GpuRenderSample sample)
    {
        sample = default;
        return false;
    }

    /// <summary>Read the most recently retired optional FG_GPU_TIMING timeline for THIS target. Sequence is target-local
    /// and monotonic, allowing asynchronous log consumers to reject repeated observations.</summary>
    bool TryGetGpuProfileSample(out GpuProfileSample sample)
    {
        sample = default;
        return false;
    }

    /// <summary>Copy one coherent target-local submitted-rect snapshot: opaque/blended instance counts are always
    /// available on supporting backends; <see cref="RectSubmittedAreaSample.HasArea"/> gates the optional
    /// <c>FG_RENDER_DIAG</c> areas and fixed top-N descriptors. Returns false when unsupported or before the target's
    /// first submit. Implementations must not expose mutable render-thread counters through this seam.</summary>
    bool TryCopyRectSubmittedAreaSample(Span<RectSubmittedAreaItem> blendedTop, out RectSubmittedAreaSample sample)
    {
        sample = default;
        return false;
    }

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
}
