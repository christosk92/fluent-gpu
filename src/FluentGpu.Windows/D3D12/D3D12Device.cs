using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
// Both FluentGpu.Foundation and TerraFX.Interop.DirectX export a ColorF; in this file ColorF always means the engine's.
using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The reference Windows RHI backend (design/subsystems/pal-rhi.md, gpu-renderer.md). Real D3D12: hardware device,
/// DIRECT command queue + fence, a DXGI flip-model swapchain on the HWND, an RTV heap, and per-frame
/// record→submit→present. Step 1 clears; the SDF rounded-rect pipeline and the DirectWrite glyph atlas layer on top.
/// <para><b>Image staging budget.</b> <see cref="DrainImageJobs"/> runs on the render thread inside the present turn,
/// and the interval between consecutive Present returns on that thread is what <c>AppHost.NotePresented</c> scores as a
/// missed vblank. Staging is CPU work in that interval (Map + padded row memcpy + Unmap per image, plus the pooled
/// texture acquire), so an unbounded drain turned a burst of landed covers straight into missed vblanks even though
/// the UI frames themselves averaged 0.5 ms. The drain therefore stages at most <see cref="UploadBytesPerTurn"/> per
/// turn (one budget on every GPU tier) and carries the first over-budget job to the next turn (the queue is drained every present turn, and
/// <see cref="HasPendingUploads"/> reports the carried job so the host keeps turning until it lands).
/// <see cref="DeferredImageUploads"/> / <see cref="DeferredImageUploadBytes"/> count those carries for the always-on
/// stats. The heap-churn half of the same defect is the upload ring in <see cref="ImageTextureStore"/>.</para>
/// </summary>
public sealed unsafe partial class D3D12Device : IGpuDevice
{
    // Back buffers == per-frame command allocators == CPU-written GPU banks (pipelines' instance uploads, compositor
    // SRV banks, query banks). 3 buffers stay — they are the CPU-side bank depth (FrameBankDepth), a memory/pipelining
    // decision. The PRESENT queue depth is a separate, LATENCY decision and is now 1.
    // WHY 1 (supersedes the old depth-2 rationale): SetMaximumFrameLatency(2) was chosen to buy one frame of slack so a
    // frame costing slightly over one refresh would not quantize to half rate at 144/165 Hz, on the assumption that the
    // second queued frame "only materializes under backpressure". MEASUREMENT killed that assumption: on a 120 Hz panel
    // with a weak Adreno the GPU costs ~5 ms of the 8.33 ms refresh on every scroll frame, so backpressure is PERMANENT
    // — the render thread sat 5-8.6 ms per frame inside the latency waitable and the frame reaching the glass had been
    // produced two vblanks earlier (DWM composes one later ⇒ ~25 ms finger-to-photon while the counter read 120 fps).
    // With depth 1 that slack is not pre-paid as latency: a frame that does go over budget now shows as ONE missed
    // vblank instead of a permanent extra frame of input lag. Terminal (AtlasEngine.r.cpp) and makepad both ship 1.
    // The waitable is a SEMAPHORE, so depth 1 makes the wait/present pairing load-bearing: see TryTakePresentSlot and
    // D3D12Swapchain.LatencyCreditHeld — every wait is a credit that exactly one Present spends.
    // HISTORY: a working-tree triple-buffering EXPERIMENT (never landed — the const was never 3 in any commit)
    // correlated with a DXGI_ERROR_DEVICE_HUNG on the Adreno after ~6.5 min of then-UNTHROTTLED image-upload bursts;
    // verdict circumstantial (docs/plans/gpu-robustness-implementation.md §Adreno). The DecodeScheduler scroll-time
    // upload throttle (the actual burst bound) landed since and STAYS ON; async device-lost recovery (also landed
    // since) turns any recurrence into a logged reset, not a dead app. NOT every bank keys off this constant
    // automatically: the formerly parity-banked
    // compositors (SliceCompositor/SurfacePool/BakedBlur) index by frameIndex % FRAME_COUNT and size heaps from FrameBankDepth —
    // FrameBankingTests (FluentGpu.Windows.Tests) asserts the derived values so the depths cannot drift apart.
    internal const uint FRAME_COUNT = 3;
    internal const int FrameBankDepth = (int)FRAME_COUNT;       // CPU-written per-frame bank depth
    // The present-queue depth is no longer a constant: the host chooses it from MEASURED GPU margin
    // (PresentQueueDepthPolicy, via SetPresentQueueDepth) — 1 while the GPU has margin (the latency argument above), 2
    // only while GPU execution approaches the refresh, where depth 1 turns a frame costing slightly more than a refresh
    // into a missed vblank on EVERY frame (half rate) instead of its true 1/gpuTime cost. Every swapchain starts at the
    // initial depth; MaxPresentQueueDepth bounds the policy, and FRAME_COUNT banks must exceed it (FrameBankingTests).
    internal const uint InitialPresentQueueDepth = 1;           // DXGI SetMaximumFrameLatency at creation — a LATENCY choice, deliberately NOT derived from FRAME_COUNT
    internal const uint MaxPresentQueueDepth = 2;
    private int _presentQueueDepth = (int)InitialPresentQueueDepth;   // render thread writes, any thread reads (Volatile)
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;   // WaitForSingleObject: the handle was signaled (TryTakePresentSlot)

    // Fence-stall watchdog (WaitFenceEventBounded, always-on — no env gate). Keyed on NO FENCE PROGRESS (completed
    // value unchanged across the 1000 ms polls), NOT raw elapsed, so legitimately long-but-progressing GPU work never
    // trips it. SOFT: emit ONE sink-routable [d3d12.stall] breadcrumb (lands in the Info log — the only evidence a
    // stall that never crosses into a formal removal would otherwise leave). HARD (frame-fence path only): force the
    // controlled InjectDeviceLost() so the open-ended hang (or a never-arriving TDR) converts into the existing fast,
    // logged device-loss recovery — the next poll sees reason!=0, records it, returns false → the async rendezvous runs.
    private const long FenceStallSoftMs = 1500;
    // 2500ms: below the ~2.7s at which a real Adreno DEVICE_HUNG was observed to self-declare (captured 0x887A0006),
    // so WE force the clean controlled RemoveDevice FIRST — recovering from our own injected loss is more reliable
    // than from a driver HUNG that has already wedged the device (DRED came back ACCESS_DENIED = too far gone).
    private const long FenceStallHardMs = 2500;

    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private IDXGIFactory4* _factory;
    private IDXGIAdapter3* _adapter3;   // render-thread-owned; QueryVideoMemoryInfo only. UI reads GpuVideoMemorySnapshot.
    private static long s_firstPresentQpc;   // QPC of the first successful Present (0 = none yet)
    private uint _rtvSize;
    // _swapChain/_rtvHeap/_backBuffers/_allocators/_frameFenceValues/_frameLatencyWaitable/_hasLatencyWaitable/
    // _swapChainFlags/_tearingSupported/_frameIndex DELETED (Phase 1, detached-window-render-isolation-
    // implementation.md §3.3/§3.5): they were the Activate/StoreActive working-copy of per-target state
    // (INCIDENT 2026-09 §1.2/§1.3). Per-target reads now go through `_f` (the current TargetFrameState — see
    // BeginTargetFrame/EndTargetFrame) or its `.Target` (the D3D12Swapchain itself); the CPU-written banks
    // (allocators, fence-value ledger, query banks) moved to the submission-keyed `_ring` (SubmissionRing).
    private ID3D12GraphicsCommandList* _cmdList;   // ALIAS: reassigned from _f.List at BeginTargetFrame — valid only inside one submit
    private ID3D12Fence* _fence;
    private ulong _fenceValue;
    private HANDLE _fenceEvent;
    // Present sync-interval 1 when true; interval 0 + ALLOW_TEARING when false (`--fg no-vsync`: diagnose present-cap vs
    // frame-cost).
    private bool _vsync = !FluentGpu.Hosting.EngineSwitches.NoVsync;
    // Host options (AppOptions.D3D12DebugLayer / D3D12Dred), set in code and fixed for the device's lifetime.
    private readonly bool _debugLayer, _dred;

    private uint _w, _h;   // ALIAS: reassigned from _f.Target.W/H at BeginTargetFrame — Resize is render-owned and never mid-submit
    private int _ringSlot;   // ALIAS: this submit's SubmissionRing slot — see the comment where it is assigned in SubmitDrawList
    // The current target's recording state — replaces _activeSwapchain (Phase 0) and every Activate/StoreActive
    // working-copy field (Phase 1). Set by BeginTargetFrame, cleared by EndTargetFrame; every stencil/layer/damage
    // helper below reads/writes it directly (the same "ambient current target" shape Activate/StoreActive used,
    // now pointing at the target's OWN state instead of a shared device-global copy).
    private TargetFrameState? _f;
    private readonly SubmissionRing _ring = new();
    private D3D12Swapchain? _primarySwapchain;
    private readonly List<D3D12Swapchain> _swapchains = new(2);
    private int _swapchainOrdinalSeq;   // forensic ring target id (D3D12Swapchain.Ordinal); 0 = none/unknown

    // ── Always-on forensic ring (INCIDENT 2026-09, docs/plans/detached-window-render-isolation-implementation.md §2.6) ─
    // The last RecordedOpRing.Capacity STATE-changing command-list calls (barriers, render-target binds, clears,
    // viewport/scissor, stencil DSV/ref, query resolves, list reset/close), each tagged with the ordinal of the target
    // the device believed it was recording for. A struct write per op — zero-alloc, no branch — and formatted into one
    // [d3d12.forensic] line only when cmdList.Close fails: the one piece of evidence a Release crash leaves, since the
    // debug layer never runs in the field. Render-owner-confined like _cmdList itself.
    private readonly RecordedOpRing _recOps = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Rec(RecordedOp op, uint a = 0, uint b = 0, ushort aux = 0)
        => _recOps.Push(op, _f?.Target.Ordinal ?? 0, a, b, aux);

    // Scissor/viewport: a run of them between two state ops collapses to one entry (last value, Aux = repeat count) so
    // a clip-heavy frame cannot evict the call Close rejected (RecordedOpRing.PushCoalesced).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecCoalesced(RecordedOp op, uint a, uint b)
        => _recOps.PushCoalesced(op, _f?.Target.Ordinal ?? 0, a, b);
    private SdfSharedResources? _sdf;
    // ONE CPU-write upload arena per frame-in-flight, shared by every geometry/image pipeline (they used to own nine
    // private FrameCount-deep rings sized for nine independent worst cases: ≈4.4 MiB permanently resident, of which a
    // real frame touched a few tens of KiB — and on UMA every CPU-visible heap is pinned host memory, so those bytes
    // were working set). Begun once per frame right after WaitForFrame, which is also the only point it may grow.
    private UploadArena? _uploadArena;
    private RoundRectPipeline? _rectPipe;
    private readonly List<RectInstance> _rectInsts = new();
    private ShadowPipeline? _shadowPipe;
    private readonly List<ShadowInstance> _shadowInsts = new();
    private ArcPipeline? _arcPipe;
    private readonly List<ArcInstance> _arcInsts = new();
    private PolylineStrokePipeline? _polylinePipe;
    private readonly List<PolylineStrokeInstance> _polylineInsts = new();
    private GradientPipeline? _gradPipe;
    private readonly List<GradientInstance> _gradInsts = new();
    private PathPipeline? _pathPipe;
    private readonly List<PathDrawItem> _pathDraws = new();
    private SeriesPipeline? _seriesPipe;
    private readonly List<SeriesInstance> _seriesInsts = new();
    private SpritePipeline? _spritePipe;
    private readonly List<SpriteInstance> _spriteInsts = new();
    private readonly List<RectF> _clipStack = new(16);
    // Tier-2 rounded clip (E9), parallel to _clipStack: the innermost rounded-box clip in effect (W <= 0 = none).
    // A rounded PushClip replaces it; a plain (rectangular) PushClip inherits the enclosing rounded clip — a reveal
    // rect nested inside a rounded surface keeps clipping the surface's corners. RoundRect-pipeline instances carry
    // the current entry; other pipelines stay scissor-only (the honest scope documented on ClipCmd).
    private readonly List<(RectF Rect, float Radius)> _roundedClipStack = new(16);
    // ── Tier-3 STENCIL PATH CLIP (gpu-renderer.md §6) ────────────────────────────────────────────────────────────────
    // ONE lazily-created, swapchain-sized D24_UNORM_S8_UINT surface, kept in DEPTH_WRITE for its whole life (no
    // barriers, ever) and ATTACHED ONLY INSIDE a scope — so every existing PSO keeps DSVFormat = UNKNOWN and every
    // frame that never clips to a path is byte-identical to before. Single-sample, like every pipeline here, which is
    // how S6's "sample-count-matched DSV" requirement is met.
    // _f!.StencilDsv/_f!.DsvHeap/_f!.StencilW/_f!.StencilH/_f!.StencilDepth/_f!.StencilScopeMasked/_f!.StencilDsvBound MOVED to
    // TargetFrameState (Phase 1 §3.1/§3.3) — read/written through `_f!.X` (SubmitDrawList's own body) or `f.X` (its
    // callees given the current TargetFrameState).
    private int _frameStencilClips, _frameStencilFallback;
    private GlyphRenderer? _glyphs;
    private ImageTextureStore? _imageTextures;
    // True on a unified-memory (integrated/APU/Adreno) adapter — the TRUE D3D12_FEATURE_DATA_ARCHITECTURE.UMA bit, set at
    // InitDevice. Distinct from GpuProfile.IsWeak (which also flags WARP): this gates the CPU-writable image-texture path
    // (WriteToSubresource, no staging buffer / copy / barrier — adreno-hang-fixes.md M1). Re-set on every device recovery.
    private bool _isUnifiedMemory;
    internal bool IsUnifiedMemory => _isUnifiedMemory;
    private ImagePipeline? _imagePipe;
    private BakedBlurCompositor? _bakedBlur;
    private FluentGpu.Hosting.Threading.BakedBlurQueue? _bakedBlurQueue;
    private readonly List<(ImageInstance inst, int imageId)> _imageDraws = new();
    private readonly List<ImageInstance> _imageRangeScratch = new(64);
    // Painter-order draw runs: a segment's non-glyph primitives in STREAM order (consecutive same-kind ops batched into
    // one draw), so a shadow correctly sits OVER the background drawn before it and UNDER the element it belongs to.
    // Without this, all shadows batch before all rects and any opaque background paints over them. Glyphs always draw last.
    // VideoHole is its OWN run class, not a Rect: a hole instance is opaque-alpha (A = VideoReady = 1) and square, so
    // inside the Rect class it would satisfy IsOpaquePlainRect and be drawn by the NO-BLEND opaque PSO as solid black —
    // the exact inverse of an erase. Its own class keeps the opaque segmentation of real rects byte-identical too.
    private enum PrimKind : byte { Rect, Shadow, Gradient, Image, Arc, Polyline, VideoHole, Path, Series, RectAdd, GradientAdd, SeriesAdd, Sprites, SpritesAdd }
    // DrawOp.SetBlend state of the stream being decoded: rect / gradient / series runs pushed while it is set take their
    // ADDITIVE variants (the same instance lists, an additive PSO). Reset at every stream start; the recorder emits
    // balanced pairs, so a stream ends SrcOver. _blendBase is the replay's floor: a feedback trail whose fresh content
    // paints Additive (FeedbackSpec.Fresh) holds it, so a nested bracket's closing SetBlend(SrcOver) stays additive.
    private bool _blendAdditive, _blendBase;
    private readonly List<(PrimKind Kind, int Count)> _runs = new();
    // Painter's-order guard for the glyph batch. RecordAll replays every glyph of a segment AFTER the segment's
    // non-glyph primitives ("text on top within a z-context"), which is only correct while nothing opaque is recorded
    // OVER text inside one segment. A modal plate over a page of text, a card fill over a list row's label in one
    // merged segment — those painted the UNDERLYING text over the covering fill (the
    // "We're glad you're here. A" fragments over the setup dialog). So: the DIP AABB of every glyph run appended
    // since the last flush is tracked here, and a later non-glyph primitive that overlaps it flushes the segment
    // first (CoverPendingText) — the batch is cut exactly where painter's order needs it and nowhere else.
    private float _pendTextL, _pendTextT, _pendTextR, _pendTextB;
    private bool _pendTextAny;
    private float _streamLw, _streamLh;   // the logical viewport of the stream being decoded (FlushSegment needs it)
    private int _frameTextCoverFlushes;   // segments cut by CoverPendingText this frame
    private int _frameImageCount;
    private int _frameImageSkipped;
    private int _frameImagesInFlight;   // draws that showed a placeholder for pixels still on a side queue
    private readonly List<GlyphInstance> _glyphInsts = new();
    private readonly List<GradGlyphInstance> _gradGlyphInsts = new();   // sub-glyph karaoke wipe (active lyric line + glow)
    private float _frameScale = 1f;
    private float _imageClockMs;
    private int _frameRectCount;
    // Of the rect instances actually RECORDED this frame, how many went through the opaque no-blend PSO vs the blended
    // SDF PSO. `rects` alone cannot distinguish a page whose fills are cheap opaque plates from one whose ladder is all
    // translucent (α < 1 by theming contract) and therefore pays a full read-modify-write per covered pixel — which is
    // exactly the "heavy-page rect overdraw" question. VideoHole punches ride the rect buffer but take the DestOut PSO
    // and are counted in NEITHER bucket.
    private int _frameRectOpaqueInsts, _frameRectBlendedInsts;
    // Existing --fg render only: submitted rect area is a fixed/no-allocation painter-work census. It deliberately
    // does NOT intersect scissors/rounded clips or union overlaps; calling it "coverage" would be false. The top-N can
    // identify geometry/alpha shape but not a source node because RectInstance carries no node identity.
#if DEBUG || FLUENTGPU_DIAG
    private static bool s_rectAreaDiag => FluentGpu.Hosting.EngineSwitches.RenderDiag;   // `--fg render`
#else
    private const bool s_rectAreaDiag = false;
#endif
    private const int BlendedRectAreaTopCount = 8;
    private double _frameRectOpaqueSubmittedPx2, _frameRectBlendedSubmittedPx2;
    private readonly RectSubmittedAreaItem[] _frameBlendedTop = new RectSubmittedAreaItem[BlendedRectAreaTopCount];
    private int _frameBlendedTopCount, _frameRectAreaOrdinal;
    private int _frameGlyphInstanceCount;
    // ── Cross-segment command-list state cache ──
    // Clip ops now update desired scissor state and flush only when pending draws need the old scissor; layer ops remain
    // hard batch breaks. Each pipeline's Record used to fully rebind its static state (root signature + PSO + constants +
    // topology + VB) per run — on a clip-heavy frame that was thousands of redundant command-list calls. The five SDF
    // pipelines share root signature + quad VB; the device tracks that shared state separately from the current PSO, so
    // SDF pipe switches bind only SetPipelineState + SRV + Draw after the first SDF run. A run whose exact pipeline is
    // already bound records only its per-run SRV offset + draw. The active scissor RECT is deduped the same way. Both
    // caches are invalidated on command-list Reset and after every compositor pass that binds its own
    // PSO/heap/scissor outside this cache (opacity/acrylic composites + blurs) — see the InvalidateCmdState call sites.
    // Barriers, OMSetRenderTargets and clears do NOT disturb these bindings, so the cache stays valid across opacity
    // group Acquire/Bind/BeginRead.
    // RectOpaque shares ALL shared SDF state (root sig / viewport / topology / quad VB) with Rect — only the PSO differs
    // (no-blend + minimal shader). So it participates in the same _sharedSdfStateBound dedup; only a PSO rebind is needed
    // when a rect run crosses the opaque↔blended boundary.
    // RectDestOut is a third rect PSO on the same shared SDF state (the video hole punch — RectPass.DestOut).
    private enum BoundPipe : byte { None, Rect, RectOpaque, RectDestOut, Shadow, Arc, Polyline, Gradient, Glyph, GradGlyph, Image, Path, Series, RectAdd, GradientAdd, SeriesAdd, Sprites, SpritesAdd }
    private BoundPipe _boundPipe;
    private bool _sharedSdfStateBound;
    private RECT _lastScissor;
    private RECT _desiredScissor;
    private bool _scissorValid;
    private bool _desiredScissorValid;
    private int _targetOriginX, _targetOriginY;
    private int _targetWidth, _targetHeight;
    private int _framePipeBinds, _framePipeBindsSkipped;      // PSO/shared-state binds vs runs that reused bound state
    private int _frameScissorSets, _frameScissorSkipped;      // RSSetScissorRects recorded vs deduped
    private int _frameSegments, _frameRuns;                   // FlushSegment calls / painter-order runs replayed
    private int _frameClipOps, _frameLayerOps;                // Push/PopClip and Push/PopLayer ops decoded
    private readonly StringTable _strings;
    private readonly bool _composited;
    private readonly float[] _clearScratch4 = new float[4];

    // ── decode-time culling (a per-tile / per-region replay) ─────────────────────────────────────────────────────────
    // A retained-tile raster replays a slice segment into ONE tile (or a degraded segment into its visible region): every
    // primitive whose conservative device AABB (inflated by its per-kind RepaintCull halo) misses the target is skipped
    // before it reaches an instance bank (the banks are per FRAME — decoding a whole slice once per tile would multiply
    // their consumption). _cullRect is in the replay's (shifted) DIP space.
    private bool _cullActive;
    private RectF _cullRect;
    // ToScissor rounds OUT (floor/ceil), so the physical scissor can be up to one device pixel wider than the DIP rect
    // the cull tests against. One DIP of slack covers that on any sane DPI (the per-kind halos are ≥ 2 DIP anyway).
    private const float CullSafetyDip = 1f;

    /// <summary>Primitive instances the last submit DROPPED because a per-pipe instance bank overflowed (a nonzero
    /// reading means a tile / frame did not paint what its stream said; the tile is not marked rastered and is rastered
    /// again next turn, after the bank grew).</summary>
    public int LastDroppedInstanceCount => DroppedInstanceCount();
    /// <summary>Glyph quads the last submit decoded (the per-frame glyph instance bank holds <c>GlyphRenderer.MaxGlyphs</c>;
    /// past that <see cref="LastDroppedInstanceCount"/> climbs).</summary>
    public int LastGlyphInstanceCount => _frameGlyphInstanceCount;
    /// <summary>Non-empty <c>FlushSegment</c> calls on the last submit — the batches the painter-order replay was cut into.</summary>
    public int LastSegmentCount => _frameSegments;
    /// <summary>Segments the last submit cut because a non-glyph primitive was recorded over text pending in the same
    /// segment (the painter's-order guard) — 0 on a stream where every fill precedes the text it sits under.</summary>
    public int LastTextCoverFlushCount => _frameTextCoverFlushes;

    private void InvalidateCmdState() { _boundPipe = BoundPipe.None; _sharedSdfStateBound = false; _scissorValid = false; }

    // Non-null only when the debug-layer option armed the layer at device creation (see InitDevice).
    private ID3D12InfoQueue* _infoQueue;
    private byte[]? _infoMsgScratch;   // allocated on the first drain — a run without the layer holds nothing

    /// <summary>Mirror the debug layer's stored messages to stderr, once per submit, then clear the queue. No-op (and
    /// no allocation) when the layer was never enabled — which is every normal run.</summary>
    private void DrainDebugLayerMessages()
    {
        if (_infoQueue == null) return;
        ulong n = _infoQueue->GetNumStoredMessages();
        if (n == 0) return;
        byte[] scratch = _infoMsgScratch ??= new byte[16384];
        for (ulong i = 0; i < n; i++)
        {
            nuint len = 0;
            if ((int)_infoQueue->GetMessage(i, null, &len) < 0 || len == 0 || len > (nuint)scratch.Length) continue;
            fixed (byte* buf = scratch)
            {
                var msg = (D3D12_MESSAGE*)buf;
                if ((int)_infoQueue->GetMessage(i, msg, &len) < 0) continue;
                int chars = msg->DescriptionByteLength > 0 ? (int)msg->DescriptionByteLength - 1 : 0;
                string text = chars > 0 ? (Marshal.PtrToStringAnsi((nint)msg->pDescription, chars) ?? "") : "";
                Console.Error.WriteLine($"[d3d12.debug] {msg->Severity} #{(int)msg->ID}: {text}");
            }
        }
        _infoQueue->ClearStoredMessages();
    }

    // DirectComposition (Mica path): the swapchain is composed onto the HWND so DWM's Mica shows through transparent pixels.
    private IDCompositionDevice* _dcomp;

    // Video compositing spine (M0): the DComp video presenter shares this device's ONE IDCompositionDevice
    // (docs/plans/video-compositing-spine-design.md §4/§13.6). Each COMPOSITED swapchain gets its own presenter bound to
    // ITS DComp root (stored on D3D12Swapchain.VideoPresenter) — the primary window and a detached video window each get
    // one; all share _dcomp. Lazily created via GetVideoPresenter(...); render-thread-confined.

    // The shared IDCompositionDevice + the primary swapchain's DComp root/UI visual, for the video presenter (same assembly).
    internal IDCompositionDevice* DcompDevice => _dcomp;
    internal D3D12Swapchain? PrimarySwapchain => _primarySwapchain;
    internal void AssertRenderThread() => AssertSubmitThread();

    /// <summary>
    /// The DRM-free video-compositing seam (<c>IVideoPresenter</c>), sharing this device's single
    /// <c>IDCompositionDevice</c> and the primary swapchain's DComp root visual. Lazily created on first access; must be
    /// touched only after the primary swapchain's DComp graph is bound (render-thread-confined). Returns null if there is
    /// no composited primary swapchain (opaque HWND path has no DComp tree).
    /// </summary>
    public FluentGpu.Pal.IVideoPresenter? GetVideoPresenter() => GetVideoPresenter(_primarySwapchain);

    /// <summary><see cref="IGpuDevice.GetVideoPresenter(ISwapchain)"/> — the per-window video presenter bound to
    /// <paramref name="swapchain"/>'s DComp root. Lazily creates + caches one presenter per COMPOSITED swapchain (stored
    /// on <see cref="D3D12Swapchain.VideoPresenter"/>); null when the target is null / not composited. Render-thread
    /// confined. The primary window and a detached video window each get their own; all share this device's one
    /// <c>IDCompositionDevice</c>.</summary>
    public FluentGpu.Pal.IVideoPresenter? GetVideoPresenter(ISwapchain swapchain)
    {
        AssertSubmitThread();
        if (swapchain is not D3D12Swapchain { Composited: true } sc) return null;
        return sc.VideoPresenter ??= new FluentGpu.Pal.Windows.DCompVideoPresenter(this, sc);
    }

    private FluentGpu.Pal.IVideoPresenter? GetVideoPresenter(D3D12Swapchain? swapchain)
        => swapchain is null ? null : GetVideoPresenter((ISwapchain)swapchain);

    /// <summary><see cref="IGpuDevice.VideoPresenter"/> — the composited-video presenter for the host's phase-11 video
    /// drain (the PRIMARY window). Delegates to <see cref="GetVideoPresenter()"/> (render-thread-confined; null unless
    /// the primary swapchain is composited).</summary>
    public FluentGpu.Pal.IVideoPresenter? VideoPresenter => GetVideoPresenter();

    /// <param name="debugLayer">Arm the D3D12 debug layer + mirror its validation messages to stderr (needs the Windows
    /// "Graphics Tools" optional feature; says so out loud when it is missing).</param>
    /// <param name="dred">Force DRED auto-breadcrumbs + page-fault reporting on (device-removed forensics).</param>
    public D3D12Device(StringTable strings, bool composited = false, bool debugLayer = false, bool dred = false)
    {
        _strings = strings;
        _composited = composited;
        _debugLayer = debugLayer;
        _dred = dred;
    }

    public void SetBakedBlurQueue(FluentGpu.Hosting.Threading.BakedBlurQueue queue) => _bakedBlurQueue = queue;

    public string BackendNameSuffix { get; private set; } = "";
    public string BackendName => "D3D12" + BackendNameSuffix;
    // Secondary popup swapchains (OS-acrylic windowed menus) submit + present on the shared device/queue alongside the
    // main window, on the direct streaming path (SubmitDrawList) — they never touch the primary's retained tiles or
    // surface pool. Per-target allocator/instance-bank reuse is already serialized by WaitForFrame(index).
    public bool SupportsSecondarySwapchains => true;

    /// <summary>Tracked live D3D12 resource totals (bytes + count) from <see cref="D3D12MemoryDiagnostics"/> — an O(1)
    /// read of the running tally for the MemCensus sampler.</summary>
    public (long bytes, int count) DiagResourceTotals => D3D12MemoryDiagnostics.LiveTotals();

    /// <summary>Last render-thread <c>QueryVideoMemoryInfo</c> snapshot (numeric copy). UI timers read this; they must
    /// not call DXGI. Empty until the first successful Present.</summary>
    public GpuVideoMemorySnapshot VideoMemorySnapshot => D3D12MemoryDiagnostics.LastVideoMemory;

    /// <summary>Process-global copy of <see cref="VideoMemorySnapshot"/> (one GPU device per process).</summary>
    public static GpuVideoMemorySnapshot LastVideoMemory => D3D12MemoryDiagnostics.LastVideoMemory;

    /// <summary>QPC timestamp of the first successful Present (0 = none yet). Startup probes subtract process start.</summary>
    public static long FirstPresentQpc => s_firstPresentQpc;

    /// <summary>One-line GPU residency summary (glyph atlas + image texture store + the shared upload arena) for the
    /// MemCensus <c>GpuDetail</c> hook. Reads the stores' census accessors; empty until the device is initialized.
    /// Tiny fixed-bucket sums (never per-frame).</summary>
    public string DiagGpuDetail =>
        _glyphs is null || _imageTextures is null
            ? ""
            : $"glyphs={_glyphs.CachedGlyphCount} runs={_glyphs.CachedRunCount} atlasGen={_glyphs.AtlasResetCount} quadPool={_glyphs.QuadPoolRetained}" +
              $" atlasEdge={_glyphs.AtlasEdge} atlasRows={_glyphs.AtlasOccupiedRows} atlasCpu={_glyphs.AtlasCpuBytes} quadPoolBytes={_glyphs.QuadPoolBytes}" +
              $" | tex: atlas={_imageTextures.AtlasImageCount} pages={_imageTextures.AtlasPageCount} pooledFree={_imageTextures.PooledTextureCount} retired={_imageTextures.RetiredCount}" +
              $" srv={_imageTextures.DescriptorSlotsUsed}/{_imageTextures.DescriptorCapacity} high={_imageTextures.DescriptorHighWater} rejected={_imageTextures.DroppedThisRun}" +
              // The shared per-frame upload arena: bytes it holds across every bank (already inside `gpu bytes`, since
              // each bank is Tracked), the per-bank capacity, the largest single-frame DEMAND seen (what "right-sized"
              // is measured against) and the refusal count (>0 ⇒ it grew and the device repainted).
              UploadArenaCensus +
              // The compositor render-target pools, split POOLED vs IN-USE. `gpu bytes` is one total and cannot say
              // whether the biggest resource class is doing work or merely resident — and on a UMA adapter all of it
              // is pinned host memory, i.e. working set. See FluentGpu.Render.LayerTargetCensus.
              LayerTargetCensusLine;

    /// <summary>The retained-tile surfaces' + the baked-blur pool's render-target census as one census-line fragment.
    /// Fixed-bucket sums (no per-frame cost — read on the MemCensus sampler's cadence, like the rest of GpuDetail).</summary>
    private string LayerTargetCensusLine
    {
        get
        {
            if (_surfaces is null && _bakedBlur is null) return "";
            FluentGpu.Render.LayerTargetCensus c = default;
            if (_surfaces is { } sp) c += sp.TargetCensus;
            if (_bakedBlur is { } bb) c += bb.TargetCensus;
            return " | rt: " + c.ToDetail();
        }
    }

    private string UploadArenaCensus
    {
        get
        {
            if (_uploadArena is null) return "";
            var p = _uploadArena.Policy;
            return $" | upload: arena={p.LiveBytes / 1024}KiB bank={p.BytesOf(p.ActiveBank) / 1024}KiB/{UploadArena.MaxBytes / 1024}KiB" +
                   $" peak={p.PeakBytes / 1024}KiB refused={p.Refusals}";
        }
    }

    /// <summary>Compact per-class + pool-state GPU residency fragment for the app's always-on <c>mem.sample</c> log
    /// line — top tracked-resource classes by bytes (<see cref="D3D12MemoryDiagnostics.TopClassesLine"/>), the
    /// compositors' render-target pool occupancy (counts, not bytes — the classes above already carry the bytes),
    /// and the shared upload arena's live/peak/refused counters. One space-separated, space-free-token string
    /// beginning with a leading space (so callers Append it directly after `gpu bytes=… resources=…`), e.g.
    /// <c> top=Image.Texture:61.2/812,Glyph.AtlasTexture:16.0/1 rt=inuse:2/free:2/pin:4 upload=arena:1.1/peak:0.4/refused:0</c>.
    /// Distinct from <see cref="DiagGpuDetail"/> (the verbose glyph/texture-store operator dump SoakProbe reads):
    /// this is sized for a periodic host log line, not manual reading. Each section is independently omitted when
    /// its store is null/empty — never throws before the device is initialized.</summary>
    public string DiagGpuCensusLine
    {
        get
        {
            var sb = new System.Text.StringBuilder(224);

            // 8, not 4: the per-bucket image classes (Image.Texture.Uma.256x256 …) plus the glyph atlas, its upload
            // banks and the layer pool already fill four rows, and the atlas PAGES — the rows that answer "is
            // thumbnail packing working at all" — fell off the end of a 4-row list every time.
            string top = D3D12MemoryDiagnostics.TopClassesLine(8);
            if (top.Length > 0) sb.Append(" top=").Append(top);

            // vram — the closing entry of the GPU book, and on UMA the only honest one. QueryVideoMemoryInfo has been
            // sampled every ~10 presents since the VRAM-pressure work; nothing ever printed it. LocalCurrentUsage is
            // the PROCESS total the OS charges us: our tracked resources PLUS everything we never see — Media
            // Foundation's decode surfaces and its own D3D11 device, the driver's arenas, PSO/shader ISA, command
            // allocators. So `untracked` is exactly the residual a working-set hunt would otherwise need VMMap to
            // find, and it is now in every field log for free. Omitted entirely until the first sample lands.
            var vm = D3D12MemoryDiagnostics.LastVideoMemory;
            if (vm.Valid)
            {
                sb.Append(" vram=used:").Append(MibOneDecimalCensus((long)vm.LocalCurrentUsage))
                  .Append("/budget:").Append(MibOneDecimalCensus((long)vm.LocalBudget))
                  .Append("/tracked:").Append(MibOneDecimalCensus(vm.TrackedResourceBytes))
                  .Append("/untracked:").Append(MibOneDecimalCensus(
                      (long)vm.LocalCurrentUsage > vm.TrackedResourceBytes
                          ? (long)vm.LocalCurrentUsage - vm.TrackedResourceBytes
                          : 0L));
                if (vm.NonLocalCurrentUsage != 0) sb.Append("/nonlocal:").Append(MibOneDecimalCensus((long)vm.NonLocalCurrentUsage));
            }

            // UI-thread reader (the MemorySampler, outside any submit): the retained-tile surfaces + the baked-blur pool.
            if (_surfaces is not null || _bakedBlur is not null)
            {
                FluentGpu.Render.LayerTargetCensus c = default;
                if (_surfaces is { } sp) c += sp.TargetCensus;
                if (_bakedBlur is { } bb) c += bb.TargetCensus;
                sb.Append(" rt=inuse:").Append(c.InUseCount).Append("/free:").Append(c.FreeCount).Append("/pin:").Append(c.RetainedCount);
            }

            if (_uploadArena is not null)
            {
                var p = _uploadArena.Policy;
                sb.Append(" upload=arena:").Append(MibOneDecimalCensus(p.LiveBytes))
                  .Append("/peak:").Append(MibOneDecimalCensus(p.PeakBytes))
                  .Append("/refused:").Append(p.Refusals);
            }

            // imgatlas — whether thumbnail page-packing is actually happening, which until now was only reachable
            // through the verbose operator dump nothing in the field calls. `pages:0` beside a large
            // Image.Texture.Uma.64x64 row is the signature of the ROW_MAJOR probe having failed; `cpuWrite:0` says
            // the discrete staging path is live. AtlasPageBytes/AtlasPagesUseGpuCopy had no callers at all before this.
            if (_imageTextures is { } imgTex)
                sb.Append(" imgatlas=pages:").Append(imgTex.AtlasPageCount)
                  .Append("/cells:").Append(imgTex.AtlasImageCount)
                  .Append("/bytes:").Append(MibOneDecimalCensus(imgTex.AtlasPageBytes))
                  .Append("/cpuWrite:").Append(imgTex.AtlasPagesUseGpuCopy ? 0 : 1);

            return sb.ToString();
        }
    }

    private static string MibOneDecimalCensus(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Operator dump: live D3D12 resources aggregated by name prefix, largest first (to stderr). The empirical
    /// "which resource class holds the climbing RAM" probe for native/UMA leak hunts. Routes to <see cref="D3D12MemoryDiagnostics"/>.</summary>
    public void DiagDumpLive(string label) => D3D12MemoryDiagnostics.DumpLive(label);

    internal ID3D12Device* Device => _device;
    internal ID3D12GraphicsCommandList* CommandList => _cmdList;

    /// <summary>Bring the adapter up NOW, before anything reads <see cref="FluentGpu.Foundation.GpuProfile"/>.
    /// <para>The constructor is inert and <see cref="InitDevice"/> runs lazily from <see cref="CreateSwapchain"/>,
    /// which the AppHost constructor calls — so a host that builds its image pipeline "after the device object exists"
    /// is still reading <c>GpuProfile.Tier == Unknown</c>, and <c>Unknown is NOT weak</c> by contract. Every budget
    /// captured at construction (the CPU pixel pool, the image-cache budget, the derived/blur budget) therefore took
    /// the DISCRETE branch on a UMA laptop and sized itself 2x too large, silently and for the process lifetime. The
    /// per-frame readers were always fine; only the ctor-captured ones were wrong, which is why this went unseen.</para>
    /// <para>Idempotent by the same <c>_device == null</c> guard <see cref="CreateSwapchain"/> uses, so the later
    /// swapchain call simply skips it, and device-loss recovery re-runs <see cref="InitDevice"/> as before. Callers
    /// that never touch a real adapter (headless, the ~40 test hosts) never reach this method at all.</para></summary>
    public void EnsureDeviceCreated()
    {
        if (_device != null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        InitDevice();
        // The boot line reports initDevice from wherever the device was ACTUALLY brought up. Left in CreateSwapchain
        // alone it would now measure a no-op and report ~0 ms, which is worse than not reporting it.
        FluentGpu.Foundation.Diag.Line(
            $"[d3d12.boot] initDevice={(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * (1000.0 / System.Diagnostics.Stopwatch.Frequency):F1}ms (early, pre-budget)");
        // Forced, bypassing the 10/60-present cadence: the host derives the weak-tier image-cache cap from
        // TryGetVramUsage (GpuMemoryBudgets.For) immediately after this call, BEFORE any swapchain/present exists to
        // drive the ordinary countdown — without this, _vramSampled stays false and the derivation falls back to the
        // unknown-budget default instead of the real LOCAL budget.
        PublishVideoMemorySnapshot(force: true);
    }

    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        AssertDeviceOwner();
        long bootT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool coldBringUp = _device == null;
        if (coldBringUp) InitDevice();
        long bootT1 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool builtPipelines = _rectPipe is null;
        EnsurePipelines();
        long bootT2 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (builtPipelines) _memoryProbeObserver?.Invoke("pipelines-created-before-swapchain");
        // ALWAYS-ON (one line, only on the swapchain call that actually brought the device/pipelines up): the two
        // costs on the critical path to first pixel. EnsurePipelines is where ~20 CreateGraphicsPipelineState calls
        // run — i.e. where the vendor's shader compiler (on the Adreno, the 49 MB qcgpuarm64xcompilercore.DLL) turns
        // cached DXBC into ISA. Without this in the shipping log, "is the pre-first-frame gap driver PSO compilation
        // or window/DComp bring-up?" is unanswerable from a release run, and the shader-cache decision stays a guess.
        if (coldBringUp || builtPipelines)
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FluentGpu.Foundation.Diag.Line($"[d3d12.boot] initDevice={(bootT1 - bootT0) * f:F1}ms pipelines={(bootT2 - bootT1) * f:F1}ms" + _pipeStageMs);
        }

        bool composited = desc.Composited || (_primarySwapchain is null && _composited);
        var target = new D3D12Swapchain(this, (HWND)desc.PresentTarget.Value,
            (uint)Math.Max(1, (int)desc.SizePx.Width), (uint)Math.Max(1, (int)desc.SizePx.Height), composited,
            desc.DesktopAcrylic, desc.AcrylicTint, desc.CornerRadiusPx, ordinal: (byte)Math.Min(255, ++_swapchainOrdinalSeq));
        InitSwapChain(target);
        _swapchains.Add(target);
        _primarySwapchain ??= target;
        // NO Activate(target) here (INCIDENT 2026-09, detached-window-render-isolation-implementation.md §1.2): creation
        // must not change which target the device is recording for — a detached child's CreateSwapchain used to swap
        // the working fields under the main window's in-flight recording. Every consumer of the working fields —
        // SubmitDrawList, Present, Resize, CaptureBgra — Activates its own target first; SizePx reads
        // _primarySwapchain.SizePx, never the working copy.
        _memoryProbeObserver?.Invoke("swapchain-created");
        return target;
    }

    // `--fg diag` cold-start attribution (runtime-gated, not Diag.CompiledIn, so the published Release bench can report).
    private static bool s_bootDiag => FluentGpu.Hosting.EngineSwitches.DiagConsole;

    // Pipeline bring-up order for the parallel stage table below (index == task slot; used for the [boot.pipe] lines).
    // The composite's surface pool / slice compositor are NOT here: they are built lazily on the primary's first
    // composite — never a device-global singleton a detached child's submit could also touch.
    private static readonly string[] s_pipeStageNames =
        ["roundrect", "shadow", "arc", "polyline", "gradient", "path", "glyphs", "image-textures", "image", "baked-blur", "series", "sprites"];

    /// <summary>
    /// Builds the SDF shared resources + the eleven draw pipelines. The SDF bring-up is SERIAL and FIRST (five of the
    /// pipelines take it as an <c>Init</c> argument and only read it afterwards); the eleven that follow run
    /// CONCURRENTLY, because cold start is dominated by their ~2 dozen serial <c>D3DCompile</c> calls (see
    /// <see cref="ShaderCompiler"/>, whose persistent DXBC disk cache covers the warm path). This is safe by
    /// construction: each pipeline only compiles its own shader sources and calls <c>ID3D12Device</c> creation methods
    /// (root signatures, PSOs, buffers, descriptor heaps), which the D3D12 spec declares free-threaded, plus the
    /// read-only <c>ID3D12CommandQueue::GetTimestampFrequency</c>. Each task builds into a LOCAL and the instance
    /// fields are published only after the join, so no caller can observe a half-initialized pipeline.
    /// </summary>
    private void EnsurePipelines()
    {
        if (_rectPipe is not null) return;
        long sdfT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _sdf = new SdfSharedResources();
        _sdf.Init(_device);
        var sdf = _sdf;
        // The shared upload arena is SERIAL and FIRST, like the SDF shared state: seven of the pipelines below take it
        // as an Init argument and only read it afterwards (it is single-toucher on the submit thread from then on).
        var arena = new UploadArena();
        arena.Init(_device);
        _uploadArena = arena;
        double sdfMs = (System.Diagnostics.Stopwatch.GetTimestamp() - sdfT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        RoundRectPipeline? rectPipe = null;
        ShadowPipeline? shadowPipe = null;
        ArcPipeline? arcPipe = null;
        PolylineStrokePipeline? polylinePipe = null;
        GradientPipeline? gradPipe = null;
        PathPipeline? pathPipe = null;
        GlyphRenderer? glyphs = null;
        ImageTextureStore? imageTextures = null;
        ImagePipeline? imagePipe = null;
        BakedBlurCompositor? bakedBlur = null;
        SeriesPipeline? seriesPipe = null;
        SpritePipeline? spritePipe = null;

        var ms = new double[s_pipeStageNames.Length];        // per-slot elapsed; distinct elements, one writer each
        var tasks = new Task[s_pipeStageNames.Length];
        Task Stage(int slot, Action build) => Task.Run(() =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            build();
            ms[slot] = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        });

        tasks[0] = Stage(0, () => { var p = new RoundRectPipeline(); p.Init(_device, sdf, arena); rectPipe = p; });
        tasks[1] = Stage(1, () => { var p = new ShadowPipeline(); p.Init(_device, sdf, arena); shadowPipe = p; });
        tasks[2] = Stage(2, () => { var p = new ArcPipeline(); p.Init(_device, sdf, arena); arcPipe = p; });
        tasks[3] = Stage(3, () => { var p = new PolylineStrokePipeline(); p.Init(_device, sdf, arena); polylinePipe = p; });
        tasks[4] = Stage(4, () => { var p = new GradientPipeline(); p.Init(_device, sdf, arena); gradPipe = p; });
        tasks[5] = Stage(5, () => { var p = new PathPipeline(); p.Init(_device, sdf, arena); pathPipe = p; });
        tasks[6] = Stage(6, () =>
        {
            var p = new GlyphRenderer();
            p.SetLivenessSource(_strings);   // reclaimed text ids → prompt run-cache eviction (quad-array recycling)
            p.Init(_device);
            glyphs = p;
        });
        tasks[7] = Stage(7, () => { var p = new ImageTextureStore(); p.Init(_device, _isUnifiedMemory); imageTextures = p; });
        tasks[8] = Stage(8, () => { var p = new ImagePipeline(); p.Init(_device, arena); imagePipe = p; });
        tasks[9] = Stage(9, () => { var p = new BakedBlurCompositor(); p.Init(_device); bakedBlur = p; });
        tasks[10] = Stage(10, () => { var p = new SeriesPipeline(); p.Init(_device, sdf, arena); seriesPipe = p; });
        tasks[11] = Stage(11, () => { var p = new SpritePipeline(); p.Init(_device, sdf, arena); spritePipe = p; });

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            // Surface the real bring-up failure (Check(...)'s InvalidOperationException) with its own stack, not a wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        // Publish AFTER the join: Task.WaitAll is the barrier, so every field below is fully constructed when read.
        _rectPipe = rectPipe;
        _shadowPipe = shadowPipe;
        _arcPipe = arcPipe;
        _polylinePipe = polylinePipe;
        _gradPipe = gradPipe;
        _pathPipe = pathPipe;
        _glyphs = glyphs;
        _imageTextures = imageTextures;
        _imagePipe = imagePipe;
        _bakedBlur = bakedBlur;
        _seriesPipe = seriesPipe;
        _spritePipe = spritePipe;
        _imageTextures!.AttachComputeQueue(bakedBlur!.ComputeQueue);   // baked derivatives are gated by its fences

        // Per-stage bring-up cost, folded into ONE always-on suffix the caller appends to its [d3d12.boot] line (the
        // stages run concurrently, so these do NOT sum to the pipelines total — the MAX is the critical path, and the
        // whole point is to see WHICH stage is it). Built once, at bring-up, into one string.
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append(" sdf=").Append(sdfMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < s_pipeStageNames.Length; i++)
                sb.Append(' ').Append(s_pipeStageNames[i]).Append('=')
                  .Append(ms[i].ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            _pipeStageMs = sb.ToString();
        }
        if (s_bootDiag)
        {
            Console.Error.WriteLine($"[boot.pipe] sdf-shared: {sdfMs:F1}ms (serial)");
            for (int i = 0; i < s_pipeStageNames.Length; i++)
                Console.Error.WriteLine($"[boot.pipe] {s_pipeStageNames[i]}: {ms[i]:F1}ms (parallel)");
        }
    }

    // Per-stage pipeline bring-up ms, formatted once by EnsurePipelines for the always-on [d3d12.boot] line.
    private string _pipeStageMs = "";

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private static void SetName(ID3D12Device* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12CommandQueue* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12CommandAllocator* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12GraphicsCommandList* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12Fence* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }

    // DXGI_ADAPTER_DESC1.Description is WCHAR[128] — in TerraFX 10.0.26100.6 an [InlineArray(128)] char buffer
    // (verified against the pinned package source), which converts implicitly to ReadOnlySpan<char>.
    private static string AdapterDescription(ref DXGI_ADAPTER_DESC1 desc)
    {
        ReadOnlySpan<char> s = desc.Description;
        int n = s.IndexOf('\0');
        return new string(n >= 0 ? s[..n] : s);
    }

    private static void ConfigureDred()
    {
        ID3D12DeviceRemovedExtendedDataSettings1* settings = null;
        if ((int)D3D12GetDebugInterface(__uuidof<ID3D12DeviceRemovedExtendedDataSettings1>(), (void**)&settings) >= 0 && settings != null)
        {
            settings->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings->SetBreadcrumbContextEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings->Release();
            return;
        }

        ID3D12DeviceRemovedExtendedDataSettings* settings0 = null;
        if ((int)D3D12GetDebugInterface(__uuidof<ID3D12DeviceRemovedExtendedDataSettings>(), (void**)&settings0) >= 0 && settings0 != null)
        {
            settings0->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings0->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings0->Release();
        }
    }

    // Render-thread-seam confinement tripwire (seam Step 0). Armed (via MarkRenderConfined) once the render thread owns
    // submit/present — i.e. when AppHost spawns it for a real windowed host (mode Async — the default — or ForceSync).
    // While armed, a SubmitDrawList/Present from any thread but Render throws deterministically UNDER FGGUARD, so a stray
    // UI-side GPU touch in async is caught in CI, never shipped. Inert on the SingleThread path (headless / internal
    // override — _renderConfined stays false ⇒ the assert is a no-op); the whole thing erases in Release (the [Conditional] + ThreadGuard vanish).
    private bool _renderConfined;
    public void MarkRenderConfined() => _renderConfined = true;
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertSubmitThread() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRender(); }

    // Device MUTATION tripwire (INCIDENT 2026-09): swapchain create/resize/dispose, WaitForGpu and the capture path
    // are legal on the render thread or on the UI thread that holds the loop parked / joined — never on an unparked
    // UI thread racing the recorder. Same arming as AssertSubmitThread (inert until MarkRenderConfined), same erasure.
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertDeviceOwner() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRenderOwner(); }

    // Seam Step 1 (ASYNC only): image Stage/Free/FlushUploads become render-confined once the host wires the upload
    // queue. Force-sync leaves the store UI-staged (no overlap), so this is a SEPARATE arm from MarkRenderConfined.
    public void MarkImageUploadsRenderConfined() => _imageTextures?.MarkRenderConfined();

    /// <summary>Pixel bytes <see cref="DrainImageJobs"/> stages per present turn before it stops and carries the rest to
    /// the next turn. 2 MiB is two 512px covers or eight 256px ones: ~0.3–0.5 ms of padded memcpy through a mapped UPLOAD
    /// heap on a discrete adapter, i.e. under a tenth of a 120 Hz turn even when the pooled-texture acquire has to grow
    /// cold, so a burst of landed covers spreads over a few turns (each still landing within the +1-frame admission
    /// contract) instead of blowing one turn past its vblank. A single job larger than the budget always stages alone
    /// on its turn — the budget bounds a TURN, it never refuses an image. A const, not a knob: the number is a property
    /// of the memcpy rate and the refresh interval, and the always-on <see cref="DeferredImageUploads"/> counter is what
    /// says whether it is being hit.</summary>
    public const int UploadBytesPerTurn = 2 * 1024 * 1024;

    // The first job that did not fit this turn's UploadBytesPerTurn, carried to the head of the next drain. The queue
    // has no peek/push-back (and belongs to another seam), so the device holds exactly one job; ownership of its pixel
    // buffer stays with us until Stage copies it and ReturnUploadBuffer hands it back on that later turn. FIFO order is
    // preserved because the drain STOPS at the held job — an eviction queued behind an upload of the same id can never
    // overtake it.
    private FluentGpu.Hosting.Threading.ImageUploadQueue.Job _heldImageJob;
    private bool _hasHeldImageJob;

    /// <summary>Cumulative count of drain turns that hit their per-turn budget (<see cref="UploadBytesPerTurn"/>) and carried a job over. The
    /// carried job always stages FIRST on the next turn (the budget never refuses the first job of a turn), so each
    /// carry is counted exactly once and the count equals the number of budget-truncated turns. Plain counter for the
    /// always-on frame stats; read from any thread as a rough gauge (render-thread writes, no fence).</summary>
    public int DeferredImageUploads { get; private set; }
    /// <summary>Cumulative pixel bytes of the jobs <see cref="DeferredImageUploads"/> counted.</summary>
    public long DeferredImageUploadBytes { get; private set; }

    // Seam Step 1 (ASYNC only): drain the UI→render image-upload queue on the render thread, just before the frame's
    // SubmitDrawList opens its command list (so a staged texture is resident before the draw that references it). Every
    // Stage/Free/return-to-pool here runs render-confined → the texture store is single-toucher, no lock.
    // Budgeted: stages up to UploadBytesPerTurn of pixels per call (evictions are free and never counted), then holds
    // the first over-budget job for the next turn — see the class summary for why the unbounded drain read as missed
    // vblanks.
    public void DrainImageJobs(FluentGpu.Hosting.Threading.ImageUploadQueue queue)
    {
        AssertSubmitThread();
        if (_imageTextures is null) return;
        if (_fence != null) _imageTextures.ReclaimCompleted(_fence->GetCompletedValue());
        int faultsBefore = _imageTextures.ResourceFaults;
        long stagedBytes = 0;
        int budgetBytes = UploadBytesPerTurn;
        while (true)
        {
            FluentGpu.Hosting.Threading.ImageUploadQueue.Job j;
            if (_hasHeldImageJob) { j = _heldImageJob; _heldImageJob = default; _hasHeldImageJob = false; }
            else if (!queue.TryDequeueJob(out j)) break;

            if (j.Evict) { _imageTextures.Free(j.Id); continue; }
            // Over budget with something already staged this turn: carry this job (and everything behind it) to the
            // next turn. A turn that has staged nothing yet always takes the job, whatever its size.
            if (j.Buffer is not null && stagedBytes > 0 && stagedBytes + j.ByteLen > budgetBytes)
            {
                _heldImageJob = j; _hasHeldImageJob = true;
                DeferredImageUploads++;
                DeferredImageUploadBytes += j.ByteLen;
                break;
            }
            var res = j.Buffer is null ? ImageUploadResult.Invalid : _imageTextures.Stage(j.Id, j.Buffer.AsSpan(0, j.ByteLen), j.W, j.H);
            if (res != ImageUploadResult.Accepted) queue.PostReject(j.Id, res);   // +1-frame async admission: the UI folds the rejection next Pump
            if (j.Buffer is not null)
            {
                queue.ReturnUploadBuffer(j.Buffer);   // ownership transferred to us; return to the host's bounded pixel pool after Stage copied it
                stagedBytes += j.ByteLen;             // counted whether or not Stage accepted — the CPU work was spent either way
            }
        }
        // A staging create/map failed. On a healthy device that was a driver OOM and this is a cheap no-op
        // (GetDeviceRemovedReason == S_OK ⇒ false); on a removed device it RECORDS the loss so the UI recovery gate
        // (threading-render-seam.md §9) arms even if no Present happens soon — a minimized/idle window can drain image
        // jobs for many turns without presenting, and the whole recovery hangs off that one recorded reason.
        if (_imageTextures.ResourceFaults != faultsBefore) NoteIfDeviceLost();
    }

    // ── Device-lost recovery (Step 4, ASYNC only; design/subsystems/threading-render-seam.md §9) ──
    // Armed by EnableAsyncDeviceLostSignaling under async. Armed ⇒ a device-removed/reset/hung failure on the render
    // thread records the reason (read by PollDeviceLost) instead of throwing an unobserved background exception, and the
    // fence waits become bounded (WaitFenceEventBounded) so a lost device can't hang the loop forever. The non-async
    // (default/force-sync) path keeps throwing on loss, unchanged.
    private int _deviceLostReason;
    private bool _signalDeviceLostInsteadOfThrow;
    public void EnableAsyncDeviceLostSignaling() => _signalDeviceLostInsteadOfThrow = true;
    public int PollDeviceLost() => System.Threading.Volatile.Read(ref _deviceLostReason);

    // Render thread: a submit/present just threw. If the device is actually removed, record the reason (so the UI recover
    // gate fires) and report true so the caller can SWALLOW the exception (keeping the render thread alive). Returns false
    // for a non-device-loss throw (a genuine bug — must NOT be masked).
    public bool NoteIfDeviceLost()
    {
        if (System.Threading.Volatile.Read(ref _deviceLostReason) != 0) return true;
        if (_device == null) return false;
        int reason = (int)_device->GetDeviceRemovedReason();
        if (reason != 0) { System.Threading.Volatile.Write(ref _deviceLostReason, reason); return true; }
        return false;
    }

    private void NoteRemovedFenceValue()
    {
        if (NoteIfDeviceLost()) return;
        // ID3D12Fence::GetCompletedValue's UINT64_MAX is itself the documented device-removal sentinel. Preserve that
        // evidence even if GetDeviceRemovedReason races and briefly returns S_OK, so the ordinary host recovery gate
        // still owns teardown/recreation instead of letting this submit continue into unresolved readback memory.
        System.Threading.Volatile.Write(ref _deviceLostReason, unchecked((int)0x887A0005u));
    }

    public void DumpDeviceLostDiagnostics(Action<string> write)
    {
        if (write is null) return;
        int recorded = System.Threading.Volatile.Read(ref _deviceLostReason);
        uint reason = _device == null ? 0u : (uint)_device->GetDeviceRemovedReason();
        var (bytes, count) = D3D12MemoryDiagnostics.LiveTotals();
        write($"[d3d12] device-lost recorded=0x{(uint)recorded:X8} currentReason=0x{reason:X8} backend={BackendName} liveResources={count} liveBytes={bytes}");
        write($"[d3d12] present dwmGlitchTotals dropped={_glitchDroppedTotal} missed={_glitchMissedTotal} late={_glitchLateTotal}" +
              $" sampledSeconds={_glitchSampledSeconds} topology={(_primarySwapchain?.PresentTopologyState ?? TopologyUnknown)}" +
              " (main-monitor-global; sampled only while presenting)");
        DumpDred(write);
    }

    private void DumpDred(Action<string> write)
    {
        if (_device == null) { write("[d3d12] DRED unavailable: device is null"); return; }
        ID3D12DeviceRemovedExtendedData1* dred = null;
        if ((int)_device->QueryInterface(__uuidof<ID3D12DeviceRemovedExtendedData1>(), (void**)&dred) < 0 || dred == null)
        {
            write("[d3d12] DRED unavailable: ID3D12DeviceRemovedExtendedData1 not supported");
            return;
        }

        try
        {
            D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT1 crumbs = default;
            HRESULT hr = dred->GetAutoBreadcrumbsOutput1(&crumbs);
            if ((int)hr >= 0) DumpDredBreadcrumbs(write, crumbs.pHeadAutoBreadcrumbNode);
            else write($"[d3d12] DRED breadcrumbs failed: 0x{(uint)hr:X8}");

            D3D12_DRED_PAGE_FAULT_OUTPUT1 fault = default;
            hr = dred->GetPageFaultAllocationOutput1(&fault);
            if ((int)hr >= 0) DumpDredPageFault(write, in fault);
            else write($"[d3d12] DRED page-fault output failed: 0x{(uint)hr:X8}");
        }
        finally
        {
            dred->Release();
        }
    }

    private static void DumpDredBreadcrumbs(Action<string> write, D3D12_AUTO_BREADCRUMB_NODE1* head)
    {
        if (head == null) { write("[d3d12] DRED breadcrumbs: none"); return; }
        int nodeIndex = 0;
        for (var node = head; node != null && nodeIndex < 8; node = node->pNext, nodeIndex++)
        {
            uint count = node->BreadcrumbCount;
            uint rawLast = node->pLastBreadcrumbValue == null ? 0u : *node->pLastBreadcrumbValue;
            uint last = count == 0 ? 0u : Math.Min(rawLast, count - 1);
            write($"[d3d12] DRED breadcrumb[{nodeIndex}] queue='{DredName(node->pCommandQueueDebugNameW, node->pCommandQueueDebugNameA)}' list='{DredName(node->pCommandListDebugNameW, node->pCommandListDebugNameA)}' last={rawLast} count={count}");
            if (node->pCommandHistory == null || count == 0) continue;
            uint start = last > 4 ? last - 4 : 0;
            uint end = Math.Min(count, last + 5);
            for (uint i = start; i < end; i++)
                write($"[d3d12]   op[{i}]={node->pCommandHistory[(int)i]}{(i == last ? " <- last" : "")}");
        }
        if (nodeIndex == 8) write("[d3d12] DRED breadcrumbs truncated at 8 nodes");
    }

    private static void DumpDredPageFault(Action<string> write, in D3D12_DRED_PAGE_FAULT_OUTPUT1 fault)
    {
        if (fault.PageFaultVA == 0 && fault.pHeadExistingAllocationNode == null && fault.pHeadRecentFreedAllocationNode == null)
        {
            write("[d3d12] DRED page fault: none");
            return;
        }
        write($"[d3d12] DRED pageFaultVA=0x{fault.PageFaultVA:X}");
        DumpDredAllocations(write, "existing", fault.pHeadExistingAllocationNode);
        DumpDredAllocations(write, "recentFreed", fault.pHeadRecentFreedAllocationNode);
    }

    private static void DumpDredAllocations(Action<string> write, string label, D3D12_DRED_ALLOCATION_NODE1* head)
    {
        if (head == null) { write($"[d3d12] DRED {label}: none"); return; }
        int i = 0;
        for (var node = head; node != null && i < 16; node = node->pNext, i++)
            write($"[d3d12] DRED {label}[{i}] type={node->AllocationType} name='{DredName(node->ObjectNameW, node->ObjectNameA)}' object=0x{(nuint)node->pObject:X}");
        if (i == 16) write($"[d3d12] DRED {label}: truncated at 16 allocations");
    }

    private static string DredName(char* wide, sbyte* ansi)
    {
        if (wide != null) return new string(wide);
        return ansi == null ? "" : (Marshal.PtrToStringAnsi((nint)ansi) ?? "");
    }

    // Async fence wait that never blocks forever on a lost device: poll GetDeviceRemovedReason on a bounded cadence and
    // bail (recording the reason) if the device died. A signaled event is verified against GetCompletedValue: device
    // removal reports UINT64_MAX there and must never masquerade as retirement of a timestamp/readback bank.
    // forceResetOnStall: the frame-fence path (gates presentation) passes true — a hard no-progress stall there is
    // forced into a clean recovery. The readback/flush path (WaitForGpu) passes false — same soft [d3d12.stall]
    // breadcrumb, but it never gates a present, so it only warns and rides the wait out.
    private bool WaitFenceEventBounded(ulong targetValue, bool forceResetOnStall)
    {
        long stallStart = System.Diagnostics.Stopwatch.GetTimestamp();
        ulong lastCompleted = ulong.MaxValue;   // sentinel: the first poll always (re)stamps the stall clock
        bool softWarned = false;
        while (true)
        {
            uint wait = WaitForSingleObject(_fenceEvent, 1000);
            ulong completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
            if (completed != ulong.MaxValue && completed >= targetValue) return true;
            int reason = (int)_device->GetDeviceRemovedReason();
            if (completed == ulong.MaxValue || reason != 0)
            {
                if (reason != 0) System.Threading.Volatile.Write(ref _deviceLostReason, reason);
                else NoteRemovedFenceValue();
                return false;
            }
            // No-progress watchdog: reset the stall clock the moment the fence advances (progressing work never trips
            // the thresholds); only a truly stuck fence accumulates toward SOFT/HARD.
            if (completed != lastCompleted)
            {
                lastCompleted = completed;
                stallStart = System.Diagnostics.Stopwatch.GetTimestamp();
                softWarned = false;
            }
            else
            {
                long stalledMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(stallStart).TotalMilliseconds;
                if (!softWarned && stalledMs >= FenceStallSoftMs)
                {
                    softWarned = true;   // ONE line per stall episode (the string is the only allocation, built once)
                    Diag.Line($"[d3d12.stall] fence stalled {stalledMs}ms target={targetValue} completed={completed} reason=0x{reason:X8}");
                }
                if (forceResetOnStall && stalledMs >= FenceStallHardMs)
                {
                    forceResetOnStall = false;   // once only — the next poll observes the removal and returns false
                    InjectDeviceLost();
                }
            }
            if (wait == 0xFFFFFFFFu) throw new InvalidOperationException("WaitForSingleObject(fence) failed.");
        }
    }

    // Controlled DEVICE_REMOVED via ID3D12Device5::RemoveDevice — does NOT TDR the whole desktop. Two callers:
    // the --fg device-lost=N=<frameN> test hook (exercises the async recovery rendezvous on real hardware), and a
    // runtime adapter switch (the app sets GpuAdapterInfo.PreferredAdapterLuid then calls this; recovery re-runs
    // InitDevice, which honors the preference first — see the selection block there).
    public void InjectDeviceLost()
    {
        ID3D12Device5* dev5;
        if (_device != null && (int)_device->QueryInterface(__uuidof<ID3D12Device5>(), (void**)&dev5) >= 0 && dev5 != null)
        {
            dev5->RemoveDevice();
            dev5->Release();
        }
    }

    // The AppHost UI recover gate polls this: consume a pending live adapter switch (Settings > About picker →
    // GpuAdapterInfo.RequestAdapterSwitch). Test-and-clear so the host can drive the device-loss rendezvous once,
    // re-running InitDevice on the newly-preferred adapter. Keeps the Engine seam TerraFX-free (the flag lives here).
    public bool ConsumeAdapterSwitchRequest() => GpuAdapterInfo.ConsumeSwitchRequest();

    private void InitDevice()
    {
        uint flags = 0;
        if (_debugLayer)
        {
            ID3D12Debug* dbg = null;
            bool armed = (int)D3D12GetDebugInterface(__uuidof<ID3D12Debug>(), (void**)&dbg) >= 0 && dbg != null;
            if (armed)
            {
                dbg->EnableDebugLayer();
                dbg->Release();
            }
            // Say so out loud. D3D12GetDebugInterface FAILS unless the "Graphics Tools" optional feature is installed,
            // and a silent failure here is how "the debug layer was clean" becomes a claim about a layer that never ran.
            Console.Error.WriteLine(armed
                ? "[d3d12.debug] debug layer ENABLED (validation messages will be mirrored to stderr)"
                : "[d3d12.debug] debug layer UNAVAILABLE — install the Windows 'Graphics Tools' optional feature");
            flags |= DXGI.DXGI_CREATE_FACTORY_DEBUG;
        }
        if (_dred) ConfigureDred();
        IDXGIFactory4* factory;
        Check(CreateDXGIFactory2(flags, __uuidof<IDXGIFactory4>(), (void**)&factory), "CreateDXGIFactory2");
        _factory = factory;

        // ── Adapter selection (pal-rhi.md §3, as-built): IDXGIFactory6.EnumAdapterByGpuPreference walks adapters in
        // HIGH_PERFORMANCE order (1803+). Cold path ⇒ raw TerraFX __uuidof QI per com-interop.md. Software adapters are
        // SKIPPED (WARP is the explicit terminal fallback, not a "winner"); a candidate whose device create fails falls
        // out of the loop — which is what makes the RecoverDevice re-run land on the next-best adapter when the previous
        // one is truly gone (§6). Pre-1803 (Factory6 QI fails) keeps the historical null-adapter default; terminal ⇒ WARP.
        ID3D12Device* device = null;
        DXGI_ADAPTER_DESC1 chosenDesc = default;
        bool haveDesc = false;
        string selectionMode = "default";

        // User-selected adapter first (Settings picker → GpuAdapterInfo.PreferredAdapterLuid, set by the app before
        // device init; a runtime switch sets it and calls InjectDeviceLost so THIS re-run applies it via recovery).
        // 0 = auto. A stale/failed preference (adapter gone, driver refused) falls through to the auto walk below.
        long preferredLuid = GpuAdapterInfo.PreferredAdapterLuid;
        if (preferredLuid != 0)
        {
            LUID pl = default;
            pl.LowPart = unchecked((uint)preferredLuid);
            pl.HighPart = (int)(preferredLuid >> 32);
            IDXGIAdapter1* pa = null;
            if ((int)_factory->EnumAdapterByLuid(pl, __uuidof<IDXGIAdapter1>(), (void**)&pa) >= 0 && pa != null)
            {
                DXGI_ADAPTER_DESC1 pd = default;
                if ((int)pa->GetDesc1(&pd) >= 0
                    && (int)D3D12CreateDevice((IUnknown*)pa, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                        __uuidof<ID3D12Device>(), (void**)&device) >= 0 && device != null)
                {
                    chosenDesc = pd; haveDesc = true; selectionMode = "preferred";
                }
                pa->Release();
            }
        }

        IDXGIFactory6* f6 = null;
        if (device == null && (int)_factory->QueryInterface(__uuidof<IDXGIFactory6>(), (void**)&f6) >= 0 && f6 != null)
        {
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1* candidate = null;
                if ((int)f6->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                        __uuidof<IDXGIAdapter1>(), (void**)&candidate) < 0 || candidate == null)
                    break;   // DXGI_ERROR_NOT_FOUND — list exhausted
                DXGI_ADAPTER_DESC1 desc = default;
                bool usable = (int)candidate->GetDesc1(&desc) >= 0
                              && (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0;
                if (usable && (int)D3D12CreateDevice((IUnknown*)candidate, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                        __uuidof<ID3D12Device>(), (void**)&device) >= 0 && device != null)
                {
                    chosenDesc = desc; haveDesc = true; selectionMode = "high-performance";
                    candidate->Release();
                    break;
                }
                candidate->Release();
            }
            f6->Release();
        }

        if (device == null)
        {
            HRESULT hr = D3D12CreateDevice(null, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device);
            if ((int)hr < 0)
            {
                // WARP fallback (VMs / RDP / no hardware GPU) — per pal-rhi.md §3b.1.
                IDXGIAdapter* warp;
                Check(_factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&warp), "EnumWarpAdapter");
                Check(D3D12CreateDevice((IUnknown*)warp, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device),
                    "D3D12CreateDevice(WARP)");
                warp->Release();
                selectionMode = "warp";
            }
        }
        // BUG FIX (was WARP-sticky): assign on EVERY path. RecoverDevice re-runs InitDevice; a recovery that lands back
        // on hardware must drop " (WARP)" — previously the suffix was only ever SET, never reset.
        BackendNameSuffix = selectionMode == "warp" ? " (WARP)" : "";
        _device = device;
        SetName(_device, "FluentGpu.Device");
        // The debug layer (AppOptions.D3D12DebugLayer) was armed above; grab its message queue so the validation output actually reaches
        // a human. Without this the layer writes only to the Win32 debug output, which a console/AOT run never shows —
        // i.e. "the debug layer is clean" would be an assertion nobody in this repo could check.
        if (_debugLayer)
        {
            ID3D12InfoQueue* iq;
            if ((int)_device->QueryInterface(__uuidof<ID3D12InfoQueue>(), (void**)&iq) >= 0 && iq != null) _infoQueue = iq;
            Console.Error.WriteLine($"[d3d12.debug] info queue {(_infoQueue != null ? "attached" : "UNAVAILABLE")}");
            // The default storage filter DENIES storage (messages only go to the Win32 debug output), so an explicit
            // allow-list is what makes the queue readable at all — without it GetNumStoredMessages is always 0 and
            // "no validation errors" would be vacuously true. INFO/MESSAGE are excluded deliberately: the runtime emits
            // hundreds of them per frame and they would bury an actual ERROR.
            if (_infoQueue != null)
            {
                _infoQueue->ClearStorageFilter();
                _infoQueue->SetMuteDebugOutput(BOOL.FALSE);
                D3D12_MESSAGE_SEVERITY* sev = stackalloc D3D12_MESSAGE_SEVERITY[3]
                {
                    D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_CORRUPTION,
                    D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_ERROR,
                    D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_WARNING,
                };
                D3D12_INFO_QUEUE_FILTER filter = default;
                filter.AllowList.NumSeverities = 3;
                filter.AllowList.pSeverityList = sev;
                _infoQueue->PushStorageFilter(&filter);
                // Under a debugger, break AT THE OFFENDING CALL instead of at the eventual Close/Execute failure.
                // The Close failure this fixes (INCIDENT above) is a downstream symptom — E_INVALIDARG from Close
                // just means "something recorded earlier was invalid"; SetBreakOnSeverity turns that into an
                // immediate int3 on the actual bad API call, with the call stack that named it. Debugger.IsAttached-
                // gated (not just the debug-layer option) so an unattended debug-layer run still only mirrors to stderr —
                // this never changes behavior for CI/harness runs, only an interactive debugging session.
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    _infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_CORRUPTION, BOOL.TRUE);
                    _infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_ERROR, BOOL.TRUE);
                }
            }
        }

        // Resolve the chosen adapter's identity by the DEVICE's own LUID — covers the default/WARP paths (which never
        // held a DXGI_ADAPTER_DESC1) and is definitionally consistent with EnsureAdapter3's resolution.
        LUID adapterLuid = _device->GetAdapterLuid();
        if (!haveDesc)
        {
            IDXGIAdapter1* byLuid = null;
            if ((int)_factory->EnumAdapterByLuid(adapterLuid, __uuidof<IDXGIAdapter1>(), (void**)&byLuid) >= 0 && byLuid != null)
            {
                haveDesc = (int)byLuid->GetDesc1(&chosenDesc) >= 0;
                byLuid->Release();
            }
        }

        // Publish a coarse GPU power tier (GpuProfile) so UI quality defaults can scale to the hardware WITHOUT a render-
        // hardware seam contract. UMA == integrated/APU (and WARP) — shares system RAM, a fraction of a discrete GPU's
        // fill rate / bandwidth ⇒ Weak; a GPU with dedicated VRAM ⇒ Strong. Best-effort: any failure leaves the tier
        // Unknown, which callers treat as the balanced default. This is what lets the lyrics depth-of-field stay smooth
        // on a weak iGPU (it auto-selects the cheap path) while a discrete GPU keeps the full effect. Kept — but now LOGGED.
        bool uma = false;
        try
        {
            D3D12_FEATURE_DATA_ARCHITECTURE arch = default;
            if ((int)_device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE, &arch, (uint)sizeof(D3D12_FEATURE_DATA_ARCHITECTURE)) >= 0)
            {
                uma = arch.UMA != 0;
                _isUnifiedMemory = uma;   // M1: gate the CPU-writable image-texture upload path (no staging/copy/barrier)
                FluentGpu.Foundation.GpuProfile.Tier = uma
                    ? FluentGpu.Foundation.GpuPowerTier.Weak
                    : FluentGpu.Foundation.GpuPowerTier.Strong;
            }
        }
        catch { /* detection is best-effort; Unknown ⇒ balanced default */ }

        bool software = selectionMode == "warp"
            || (haveDesc && (chosenDesc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0);
        string adapterName = haveDesc ? AdapterDescription(ref chosenDesc)
                           : selectionMode == "warp" ? "Microsoft Basic Render Driver (WARP)" : "<unknown>";
        FluentGpu.Foundation.GpuProfile.AdapterName = adapterName;
        FluentGpu.Foundation.GpuProfile.IsSoftwareAdapter = software;
        GpuAdapterInfo.Publish(adapterLuid);   // sibling device creators (D3D11 video decode) pin to this GPU

        // ALWAYS-ON identity line (one per device init/recovery — the Release-build evidence of WHICH GPU ran).
        FluentGpu.Foundation.Diag.Line(
            $"[d3d12.adapter] mode={selectionMode} desc=\"{adapterName}\"" +
            $" vendorId=0x{chosenDesc.VendorId:X4} deviceId=0x{chosenDesc.DeviceId:X4}" +
            $" vramMB={(long)(chosenDesc.DedicatedVideoMemory / (1024 * 1024))} sharedMB={(long)(chosenDesc.SharedSystemMemory / (1024 * 1024))}" +
            $" luid=0x{adapterLuid.HighPart:X8}:{adapterLuid.LowPart:X8} uma={uma} software={software}" +
            $" tier={FluentGpu.Foundation.GpuProfile.Tier}");

        _memoryProbeObserver?.Invoke("native-device-created-before-queue");
        D3D12_COMMAND_QUEUE_DESC qd = default;
        qd.Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT;
        qd.Flags = D3D12_COMMAND_QUEUE_FLAGS.D3D12_COMMAND_QUEUE_FLAG_NONE;
        ID3D12CommandQueue* queue;
        Check(_device->CreateCommandQueue(&qd, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
        _queue = queue;
        SetName(_queue, "FluentGpu.CommandQueue");
        _memoryProbeObserver?.Invoke("queue-created-before-ring");

        // The allocator RING is device-shared, sized to SubmissionRing.Depth (Phase 1 §3.2 — was FRAME_COUNT-deep and
        // device-global; a per-target command LIST is created per swapchain instead, in InitSwapChain, once that
        // target exists — there is no more single device-global _cmdList to create here).
        for (int i = 0; i < SubmissionRing.Depth; i++)
        {
            ID3D12CommandAllocator* alloc;
            Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator");
            _ring.Allocators[i] = alloc;
            SetName(alloc, $"FluentGpu.CommandAllocator[{i}]");
        }

        ID3D12Fence* fence;
        Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence), "CreateFence");
        _fence = fence;
        SetName(_fence, "FluentGpu.FrameFence");
        _fenceValue = 0;
        _fenceEvent = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);

        _rtvSize = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        _memoryProbeObserver?.Invoke("allocator-ring-list-fence-created");
    }

    private void InitSwapChain(D3D12Swapchain target)
    {
        DXGI_SWAP_CHAIN_DESC1 sd = default;
        sd.Width = target.W;
        sd.Height = target.H;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.Stereo = BOOL.FALSE;
        sd.SampleDesc.Count = 1;
        sd.SampleDesc.Quality = 0;
        sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = FRAME_COUNT;
        sd.Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH;
        // The PRIMARY target is FLIP_SEQUENTIAL: each back buffer keeps its pixels, so a frame recomposites only what
        // changed and presents with Present1 dirty rects (D3D12Device.PartialPresent.cs). Popups and detached windows
        // replay whole frames (the direct route) and keep FLIP_DISCARD.
        target.SequentialFlip = _primarySwapchain is null || ReferenceEquals(target, _primarySwapchain);
        target.PpEpoch++;
        sd.SwapEffect = target.SequentialFlip ? DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL : DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
        sd.AlphaMode = target.Composited ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;

        // Latency-waitable swapchain (canon: pal-rhi.md §5.1 / budgets.md) — lets us bound queued frames and wait efficiently
        // for present-readiness instead of blocking deep on the GPU fence. ALLOW_TEARING (hwnd + vsync-off only) needs both the
        // swapchain flag here AND the matching present flag, gated by factory support.
        target.TearingSupported = !target.Composited && CheckTearingSupport();
        uint flags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        if (target.TearingSupported) flags |= (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
        target.SwapChainFlags = flags;
        sd.Flags = flags;

        IDXGISwapChain1* sc1;
        if (target.Composited)
            Check(_factory->CreateSwapChainForComposition((IUnknown*)_queue, &sd, null, &sc1), "CreateSwapChainForComposition");
        else
            Check(_factory->CreateSwapChainForHwnd((IUnknown*)_queue, target.Hwnd, &sd, null, null, &sc1), "CreateSwapChainForHwnd");

        IDXGISwapChain3* sc3;
        Check(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3");
        sc1->Release();
        target.SwapChain = sc3;

        // IDXGISwapChain3 : IDXGISwapChain2 — cap the queued frames and grab the latency waitable (created above via the flag).
        // The primary follows the device's chosen depth; every other target its OWN (a pop-out's depth policy sets it, and a
        // device-loss rebuild of its swapchain must come back at that depth, not reset it).
        uint depthNow = ReferenceEquals(target, _primarySwapchain) ? (uint)Volatile.Read(ref _presentQueueDepth) : (uint)Volatile.Read(ref target.PresentQueueDepth);
        Check(target.SwapChain->SetMaximumFrameLatency(depthNow), "SetMaximumFrameLatency");
        target.FrameLatencyWaitable = target.SwapChain->GetFrameLatencyWaitableObject();
        target.HasLatencyWaitable = target.FrameLatencyWaitable != HANDLE.NULL;
        // A brand-new waitable (fresh swapchain, or a device-loss rebuild): any credit taken against the OLD handle is
        // void. The new semaphore starts signaled, so the next TryTakePresentSlot returns at once — reserving the
        // slot rather than skipping it is also the only safe direction (skipping would present into a full queue).
        target.LatencyCreditHeld = false;
        // Always-on, once per swapchain: the present-queue depth is a LATENCY decision that is invisible from the
        // outside (a queue two frames deep still reports a healthy frame rate — that is exactly how depth 2 hid ~1
        // frame of input lag until it was measured). Logged so any later session can tell from the log alone which
        // pacing contract the binary shipped with, the way [compositor-clock] now names the clock's state.
        if (!s_loggedPresentContract)
        {
            s_loggedPresentContract = true;
            Diag.Line($"[d3d12.present] maxFrameLatency={depthNow} (adaptive {InitialPresentQueueDepth}..{MaxPresentQueueDepth}) buffers={FRAME_COUNT}"
                      + $" waitable={(target.HasLatencyWaitable ? "yes" : "no")} slotWait=pre-acquire");
        }

        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hd.NumDescriptors = FRAME_COUNT;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        ID3D12DescriptorHeap* heap;
        Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap");
        target.RtvHeap = heap;
        D3D12MemoryDiagnostics.Track(target.RtvHeap, "Swapchain.RtvHeap", (ulong)FRAME_COUNT * _rtvSize);

        if (target.DesktopAcrylic)
        {
            // Desktop-sampling acrylic popup (the WinUI MenuFlyout material): host the swapchain in a Windows.UI.Composition
            // tree over a host-backdrop (blurred desktop) + tint, on the popup HWND — NOT DirectComposition (which has no
            // host backdrop). Rounded corners are a composition geometric clip on the backdrop group (the HWND carries NO
            // DWM rounding/shadow — that chrome would be full-size and can't reveal with the open animation).
            target.Backdrop = new CompositionBackdrop(target.Hwnd, (IUnknown*)target.SwapChain, target.AcrylicTint, target.CornerRadiusPx);
            target.Backdrop.SetBounds(target.W, target.H);
        }
        else if (target.Composited)
        {
            // Async render-thread seam (DIM on-screen fix): the DirectComposition device/target/visual are a RENDER-THREAD
            // SOLE-COM-OWNER (threading-render-seam.md §1). The SAME thread that Presents must create+own+Commit the visual
            // tree — under async, presenting the composited flip swapchain from the render thread while the DComp graph was
            // created+committed on the UI thread makes DWM composite it DIM/stale. InitSwapChain runs UI-side (ctor) or
            // render-side (RecoverDevice), so DEFER the bind: BindDComp runs it on the PRESENTING thread (lazily on first
            // Present, or explicitly in the render-confined RecoverDevice). CaptureBgra reads the back buffer and never hit
            // this — the blind spot that hid the dim composite. The IDXGISwapChain + RTVs above are queue-scoped (safe here).
            target.DcompBindPending = true;
        }

        CreateRtvs(target);
        // ONE command list per target (Phase 1 §3.1 — was ONE device-global list, created once in InitDevice). Any
        // ring allocator is fine here: the first real submit's Reset() supplies the actual bank. This runs on the
        // creating thread (UI ctor, or the render thread inside RecoverDevice) — cold path, never mid-record.
        ID3D12GraphicsCommandList* list;
        // Created as an ID3D12GraphicsCommandList4 (GEN-COM: the render-pass surface — BeginRenderPass/EndRenderPass),
        // returned CLOSED; the same COM pointer serves the base-interface calls (List4 derives from List).
        void* list4;
        Check((HRESULT)D3D12SideQueues.CreateClosedList(_device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            _ring.Allocators[0], asList4: true, out list4), "CreateCommandList(List4)");
        list = (ID3D12GraphicsCommandList*)list4;
        target.Frame.List = list;
        target.Frame.List4 = list4;
        SetName(target.Frame.List, $"FluentGpu.CommandList[{target.Ordinal}]");
        SamplePresentTopology(target);
    }

    private void EnsureDComp()
    {
        AssertSubmitThread();   // seam: the DComp device is render-thread-confined once a render thread exists (no-op in pure single-thread)
        if (_dcomp != null) return;
        IDCompositionDevice* dc;
        Check(DCompositionCreateDevice(null, __uuidof<IDCompositionDevice>(), (void**)&dc), "DCompositionCreateDevice");
        _dcomp = dc;
    }

    // Bind (or rebind) a composited swapchain's DirectComposition target/visual on the PRESENTING thread. Deferred out of
    // InitSwapChain (which runs UI-side in the ctor) so the whole DComp graph is created + Commit()ed by the same thread
    // that Presents — the SOLE-COM-OWNER contract (threading-render-seam.md §1), the fix for the async DIM on-screen
    // composite. Runs once per swapchain lifetime (clears DcompBindPending); the SetContent binding survives ResizeBuffers,
    // so a resize does NOT re-arm it. AssertSubmitThread is a no-op in the pure single-thread path (not render-confined).
    private void BindDComp(D3D12Swapchain target)
    {
        AssertSubmitThread();
        EnsureDComp();
        IDCompositionTarget* dcompTarget;
        Check(_dcomp->CreateTargetForHwnd(target.Hwnd, BOOL.TRUE, &dcompTarget), "CreateTargetForHwnd");
        target.DcompTarget = dcompTarget;
        // Video spine (M0, docs/plans/video-compositing-spine-design.md §3.2): a ROOT visual wrapping the UI child, so
        // video child visuals can be lazily inserted z-BELOW the UI. With zero video children this is behaviorally
        // identical to the old single-visual tree (root → one UI child on top). Back-compat: nothing about the swapchain,
        // RTVs, present, or the SetContent-survives-ResizeBuffers property changes.
        IDCompositionVisual* root;
        Check(_dcomp->CreateVisual(&root), "CreateVisual(root)");
        target.DcompRoot = root;
        IDCompositionVisual* visual;
        Check(_dcomp->CreateVisual(&visual), "CreateVisual(ui)");
        target.DcompVisual = visual;
        Check(target.DcompVisual->SetContent((IUnknown*)target.SwapChain), "Visual.SetContent");
        // UI child on TOP of the root (insertAbove=TRUE, ref=null). Video children go under it via AddVisual(child,
        // insertAbove=FALSE, ref=uiVisual) in DCompVideoPresenter — strictly beneath, revealed by the premul-0 hole.
        Check(target.DcompRoot->AddVisual(target.DcompVisual, BOOL.TRUE, null), "Root.AddVisual(ui)");
        Check(target.DcompTarget->SetRoot(target.DcompRoot), "Target.SetRoot(root)");
        Check(_dcomp->Commit(), "DComp.Commit");
        target.DcompBindPending = false;
        // No re-attach of video children here: DComp visuals cannot move between IDCompositionDevice instances, so a device
        // recovery disposes the presenter (RecoverDevice) and the video registry rebuilds its surfaces on a fresh one; a
        // child created before this first lazy bind attaches on the presenter's next Commit.
    }

    // Producer swapchains that render into engine-owned shareable DComp surface handles (M0 test surfaces). Kept alive
    // for the device lifetime so the DWM-side CreateSurfaceFromHandle reads live pixels; released in Dispose.
    private readonly List<nint> _testSurfaceSwapchains = new();

    /// <summary>
    /// M0 (docs/plans/video-phase1-plan.md §4, correction #4): create a REAL shareable DirectComposition surface via
    /// <c>DCompositionCreateSurfaceHandle</c>, render a recognizable test pattern into it through a
    /// composition-surface-handle DXGI swapchain, and return the handle. The video presenter binds it with
    /// <c>CreateSurfaceFromHandle</c> — so <c>BindSurfaceHandle</c> is exercised end-to-end with no media/decoder/DRM.
    /// The pattern is a two-tone split (magenta right / cyan left) so the composited blend is unmistakably the surface,
    /// not a flat fill. Render-thread-confined; the producer swapchain is retained for the device lifetime.
    /// </summary>
    public nuint CreateEngineTestSurfaceHandle(uint w, uint h)
    {
        AssertSubmitThread();
        if (w < 1) w = 1; if (h < 1) h = 1;

        // 1. The shareable surface handle (COMPOSITIONOBJECT_ALL_ACCESS). This is the primitive a plain CreateSurface
        //    lacks — it is what makes the handle path real (correction #4).
        HANDLE surfHandle;
        Check(DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS, null, &surfHandle), "DCompositionCreateSurfaceHandle");

        // 2. A DXGI composition-surface-handle swapchain that renders INTO that handle (the producer side).
        IDXGIFactoryMedia* factoryMedia;
        Check(_factory->QueryInterface(__uuidof<IDXGIFactoryMedia>(), (void**)&factoryMedia), "QI IDXGIFactoryMedia");
        DXGI_SWAP_CHAIN_DESC1 sd = default;
        sd.Width = w;
        sd.Height = h;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = FRAME_COUNT;
        sd.Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH;
        sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;   // opaque test pattern (child must present opaque content)
        IDXGISwapChain1* sc1;
        Check(factoryMedia->CreateSwapChainForCompositionSurfaceHandle((IUnknown*)_queue, surfHandle, &sd, null, &sc1),
            "CreateSwapChainForCompositionSurfaceHandle");
        factoryMedia->Release();
        IDXGISwapChain3* sc3;
        Check(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3 (test surface)");
        sc1->Release();

        // 3. Render one frame: clear buffer 0 to magenta, then the left half to cyan (a two-tone split). One-off
        //    allocator + command list (this runs once at setup, single-threaded).
        ID3D12Resource* buf;
        Check(sc3->GetBuffer(0, __uuidof<ID3D12Resource>(), (void**)&buf), "GetBuffer(test surface)");
        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hd.NumDescriptors = 1;
        ID3D12DescriptorHeap* rtvHeap;
        Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&rtvHeap), "CreateDescriptorHeap(test surface)");
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        _device->CreateRenderTargetView(buf, null, rtv);

        ID3D12CommandAllocator* alloc;
        Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator(test surface)");
        ID3D12GraphicsCommandList* cl;
        Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, null,
            __uuidof<ID3D12GraphicsCommandList>(), (void**)&cl), "CreateCommandList(test surface)");

        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = buf;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        b.Anonymous.Transition.StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        b.Anonymous.Transition.StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
        cl->ResourceBarrier(1, &b);

        float* magenta = stackalloc float[4] { 1f, 0f, 1f, 1f };
        float* cyan = stackalloc float[4] { 0f, 1f, 1f, 1f };
        cl->ClearRenderTargetView(rtv, magenta, 0, null);
        RECT leftHalf = default; leftHalf.left = 0; leftHalf.top = 0; leftHalf.right = (int)(w / 2); leftHalf.bottom = (int)h;
        cl->ClearRenderTargetView(rtv, cyan, 1, &leftHalf);

        b.Anonymous.Transition.StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
        b.Anonymous.Transition.StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        cl->ResourceBarrier(1, &b);
        // Same DrainDebugLayerMessages-on-failure fix as the main submit's Close (:2234) — this is a ONE-OFF list
        // (own allocator + list, released unconditionally right below), so there is no shared-cmdList Reset hazard
        // to recover from; try/finally is enough to make a validation failure here diagnosable too.
        try
        {
            Check(cl->Close(), "cmdList.Close(test surface)");
        }
        finally
        {
            DrainDebugLayerMessages();
        }
        ID3D12CommandList* rawList = (ID3D12CommandList*)cl;
        _queue->ExecuteCommandLists(1, &rawList);
        WaitForGpu();

        // Present into the composition surface handle so DWM can read the painted pixels.
        Check((HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChainVtbl.Present(sc3, 0, 0), "Present(test surface)");
        WaitForGpu();

        cl->Release();
        alloc->Release();
        buf->Release();
        rtvHeap->Release();
        _testSurfaceSwapchains.Add((nint)sc3);   // keep the producer alive; released in Dispose

        return (nuint)(nint)surfHandle;
    }

    private void CreateRtvs(D3D12Swapchain target)
    {
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = target.RtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            ID3D12Resource* buf;
            Check(target.SwapChain->GetBuffer(i, __uuidof<ID3D12Resource>(), (void**)&buf), "GetBuffer");
            target.BackBuffers[i] = buf;
            D3D12MemoryDiagnostics.Track(buf, $"Swapchain.BackBuffer[{i}] {target.W}x{target.H}", (ulong)target.W * target.H * 4UL);
            _device->CreateRenderTargetView(buf, null, rtv);
            rtv.ptr += _rtvSize;
        }
    }

    // Replaces Activate (Phase 1, §3.4): no more copy-in to device working fields — `_f` (and the `_w/_h/_cmdList`
    // aliases, valid only for the duration of this call) point straight at the target's OWN TargetFrameState, so a
    // detached child's CreateSwapchain/DisposeSwapchain can never again clobber the main window's in-flight
    // recording state (INCIDENT 2026-09 §1.2/§1.3 — the class of bug Activate/StoreActive made possible by
    // construction).
    private void BeginTargetFrame(TargetFrameState f)
    {
        AssertDeviceOwner();
        _f = f;
        _w = f.Target.W;
        _h = f.Target.H;
        _cmdList = f.List;
        _list4 = f.List4;
    }

    // Replaces StoreActive (Phase 1, §3.4): there is no working copy to write back — `_f`/`_w`/`_h`/`_cmdList` were
    // never anything but a reference to the target's own state. Clearing them here means a stray post-submit touch
    // (e.g. a bug that calls a stencil/layer helper outside BeginTargetFrame/EndTargetFrame) faults immediately via
    // the null-forgiving `_f!` accessors instead of silently reading whichever target happened to submit last.
    private void EndTargetFrame()
    {
        _f = null;
        _cmdList = null;
        _list4 = null;
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        if (_primarySwapchain is null) throw new InvalidOperationException("CreateSwapchain must be called before SubmitDrawList.");
        SubmitDrawList(drawList, sortKeys, in ctx, _primarySwapchain);
    }

    private Action? _glyphGrowthFence;

    // All potentially packing text/icon work happens before command-list reset and before any subsystem stamps
    // retirements with _fenceValue + 1. Rare atlas growth may drain the whole device and advance that fence value.
    // Use the engine-owned opcode framing table; unknown/truncated streams cannot be partially preflighted.
    private void PrepareGlyphs(ReadOnlySpan<byte> commands)
    {
        _glyphs!.BeginPreparation(_glyphGrowthFence ??= WaitForGpu);
        try
        {
            int offset = 0;
            while (offset < commands.Length)
            {
                if (commands.Length - offset < sizeof(int)) throw new InvalidOperationException("Truncated glyph preflight opcode.");
                DrawOp op = (DrawOp)MemoryMarshal.Read<int>(commands.Slice(offset));
                offset += sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int bytes) || bytes > commands.Length - offset)
                    throw new InvalidOperationException("Unknown or truncated glyph preflight payload.");
                var payload = commands.Slice(offset, bytes);
                offset += bytes;
                switch (op)
                {
                    case DrawOp.DrawGlyphRun:
                    {
                        var g = MemoryMarshal.Read<DrawGlyphRunCmd>(payload);
                        string text = _strings.Resolve(g.Text);
                        if (text.Length == 0) break;
                        _glyphs.LayoutRun(g.Text, g.Family, text, _strings.Resolve(g.Family), g.FontSize, g.Weight,
                            g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines, g.CharSpacing,
                            g.LineHeight, g.LineStacking, g.LineBounds, g.Color, _frameScale, g.Transform,
                            g.Opacity, _glyphInsts, g.SpanRunId, g.ForceColor != 0, g.InMotion * (1f / 255f));
                        break;
                    }
                    case DrawOp.DrawGlyphRunGradient:
                    {
                        var g = MemoryMarshal.Read<DrawGlyphRunGradientCmd>(payload);
                        string text = _strings.Resolve(g.Text);
                        if (text.Length == 0) break;
                        _glyphs.LayoutRunGradient(g.Text, g.Family, text, _strings.Resolve(g.Family), g.FontSize,
                            g.Weight, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines,
                            g.CharSpacing, g.LineHeight, g.LineStacking, g.LineBounds, g.Before, g.After,
                            g.Split, g.Softness, g.Lift, _frameScale, g.Transform, g.Opacity,
                            _gradGlyphInsts, _glyphInsts, g.SpanRunId, g.InMotion * (1f / 255f));
                        break;
                    }
                    case DrawOp.DrawIconMask:
                    {
                        var icon = MemoryMarshal.Read<DrawIconMaskCmd>(payload);
                        if (icon.PathId == 0 || icon.Tint.A <= 0f || icon.Rect.W <= 0f || icon.Rect.H <= 0f) break;
                        int width = Math.Max(1, (int)MathF.Round(icon.Rect.W * _frameScale));
                        int height = Math.Max(1, (int)MathF.Round(icon.Rect.H * _frameScale));
                        if (_glyphs.TryGetIconUv(icon.PathId, width, height, out _, out _, out _, out _)) break;
                        int count = checked(width * height);
                        byte[] mask = ArrayPool<byte>.Shared.Rent(count);
                        try
                        {
                            IconGeometryTable.Shared.Rasterize(icon.PathId, width, height, mask.AsSpan(0, count));
                            _glyphs.PackIconMask(icon.PathId, width, height, mask, out _, out _, out _, out _);
                        }
                        finally { ArrayPool<byte>.Shared.Return(mask); }
                        break;
                    }
                }
            }
        }
        finally { _glyphs.EndPreparation(); }
    }

    /// <summary>
    /// The DIRECT route: clear + replay one stream straight into a swapchain's back buffer inside one render pass (CLEAR →
    /// STORE — no <c>ClearRenderTargetView</c>). Only targets that do not composite take it — windowed popups and
    /// detached pop-outs (OS-backed acrylic, no engine layers: a stray PushLayer draws its acrylic fallback flat). The
    /// primary window composites its retained tiles (<see cref="SubmitComposite"/>).
    /// </summary>
    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
    {
        if (target is not D3D12Swapchain sc || sc.Device != this || sc.Disposed)
            throw new InvalidOperationException("Submit target is not a live D3D12 swapchain from this device.");
        AssertSubmitThread();   // seam Step 0: when render-confined (force-sync/async), only the render thread may submit
        TargetFrameState f = sc.Frame;
        _stencilFloorW = 0; _stencilFloorH = 0;
        int slot = OpenSubmit(sc, f);
        _frameScale = ctx.Scale <= 0f ? 1f : ctx.Scale;
        _glyphs!.BeginFrame(slot);
        PrepareGlyphs(drawList);
        bool isPrimaryTarget = ReferenceEquals(sc, _primarySwapchain);
        if (isPrimaryTarget) PpInvalidate();   // a direct replay rewrote the back buffer outside the partial bookkeeping
        BeginRecording(sc, f, slot, isPrimaryTarget);
        if ((_frameKnockouts & GpuKnockouts.ClearOnly) != 0) drawList = default;
        PassBoundary(GpuPassKind.Clear, (int)sc.W, (int)sc.H);

        ID3D12Resource* backBuffer = sc.BackBuffers[f.FrameIndex];
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = sc.RtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += f.FrameIndex * _rtvSize;

        _imageClockMs = ctx.ImageClockMs;
        ResetFrameCounters();
        BeginPipesFrame(slot);
        float lw = _w / _frameScale, lh = _h / _frameScale;

        f.TextRepaintPending = false;
        BeginPass(rtv, (int)_w, (int)_h, PassLoad.Clear, ctx.Clear);
        PassToScene();   // pass timeline — boundary: the clear is part of the pass begin, scene drawing starts
        SetFullViewport();
        SubmitStreaming(drawList, lw, lh, rtv);
        EndPassIfOpen();
        NoteFaithfulness(f);
        PublishDecodeDiagnostics();

        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        EndRecording(sc, f, slot, RepaintRoute.FullDirect, backBufferTransitions: _frameBackBufferTransitions);
    }

    /// <summary>A frame whose decode dropped instances, whose upload arena refused a run, or whose glyph atlas reset
    /// mid-record did not paint what its stream said: the host owes one more full, un-skippable frame.</summary>
    private void NoteFaithfulness(TargetFrameState f)
    {
        if (_glyphs!.DroppedInstances > 0)
        {
            f.TextRepaintPending = true;
            Diag.Count("d3d12", "glyphBankOverflowRepaint");
        }
        if (_uploadArena!.RefusedThisFrame)
        {
            f.TextRepaintPending = true;
            Diag.Count("d3d12", "uploadArenaGrowthRepaint");
        }
        if (_glyphs!.AtlasResetPending)
        {
            f.TextRepaintPending = true;
            Diag.Count("d3d12", "textAtlasRepaint");
        }
    }

    private void PublishDecodeDiagnostics()
    {
        Diag.Set("d3d12", "segments", _frameSegments);
        Diag.Set("d3d12", "textCoverFlushes", _frameTextCoverFlushes);
        Diag.Set("d3d12", "runs", _frameRuns);
        Diag.Set("d3d12", "clipOps", _frameClipOps);
        // Tier-3 stencil path clips (gpu-renderer.md §6). ALWAYS ON — `stencilFallback` is the honesty counter: draws
        // recorded INSIDE a scope by a pipeline with no EQUAL-tested clone (Shadow / Arc / Polyline / Series / the DestOut video
        // hole), which are clipped by the scope's SCISSOR only. A scope that could not mask at all counts once.
        Diag.Set("d3d12", "stencilClips", _frameStencilClips);
        Diag.Set("d3d12", "stencilFallback", _frameStencilFallback);
        Diag.Set("d3d12", "layerOps", _frameLayerOps);
        Diag.Set("d3d12", "pipeBinds", _framePipeBinds);
        Diag.Set("d3d12", "pipeBindsSkipped", _framePipeBindsSkipped);
        Diag.Set("d3d12", "scissorSets", _frameScissorSets);
        Diag.Set("d3d12", "scissorSkipped", _frameScissorSkipped);
        Diag.Set("d3d12", "instancesDropped", DroppedInstanceCount());
        Diag.Set("d3d12", "rects", _frameRectCount);
        Diag.Set("d3d12", "rectInstOpaque", _frameRectOpaqueInsts);
        Diag.Set("d3d12", "rectInstBlended", _frameRectBlendedInsts);
        Diag.Set("d3d12", "rectSubmittedOpaquePx2", _frameRectOpaqueSubmittedPx2);
        Diag.Set("d3d12", "rectSubmittedBlendedPx2", _frameRectBlendedSubmittedPx2);
        Diag.Set("d3d12", "glyphInstances", _frameGlyphInstanceCount);
        Diag.Set("d3d12", "gradGlyphDropped", _glyphs?.GradInstancesDropped ?? 0);
        Diag.Set("d3d12", "gradGlyphPeak", _glyphs?.GradInstancePeak ?? 0);
        Diag.Set("d3d12", "gradGlyphBudget", GlyphRenderer.GradInstanceBudget);
        Diag.Set("d3d12", "images", _frameImageCount);
        Diag.Set("d3d12", "imagesSkipped", _frameImageSkipped);
        Diag.Set("d3d12", "imageAtlas", _imageTextures!.AtlasImages);
        Diag.Set("d3d12", "imagePool", _imageTextures.PoolImages);
        Diag.Set("d3d12", "imageSrvUsed", _imageTextures.DescriptorSlotsUsed);
        Diag.Set("d3d12", "imageSrvHighWater", _imageTextures.DescriptorHighWater);
        Diag.Set("d3d12", "imageUploadRejected", _imageTextures.DroppedThisRun);
        Diag.Set("path", "draws", _pathPipe?.DrawsThisFrame ?? 0);
        Diag.Set("path", "uploadBytes", _pathPipe?.UploadBytesThisFrame ?? 0L);
        Diag.Set("path", "dropped", _pathPipe?.DroppedInstances ?? 0);
        Diag.Set("series", "dropped", _seriesPipe?.DroppedInstances ?? 0);
        Diag.Set("sprites", "dropped", _spritePipe?.DroppedInstances ?? 0);
        Diag.Set("text.atlas", "cachedGlyphs", _glyphs!.CachedGlyphs);
        Diag.Set("text.atlas", "nonZeroBytes", _glyphs.AtlasNonZero);
        Diag.Set("text.run", "cachedRuns", _glyphs.CachedRuns);
        Diag.Set("text.run", "runsCached", _glyphs.RunsCached);
        Diag.Set("text.run", "runsShaped", _glyphs.RunsShaped);
    }

    /// <summary>
    /// The first half of every submit: bind the target's recording state, recreate a stale-sized stencil DSV (EAGERLY —
    /// before anything stamps a fence-gated retirement this frame), pay the latency / back-buffer / ring-slot waits, and
    /// pick the ring slot. Returns it.
    /// </summary>
    private int OpenSubmit(D3D12Swapchain sc, TargetFrameState f)
    {
        BeginTargetFrame(f);
        // Per-target stencil DSV (CANDIDATE FIX, INCIDENT 2026-09; Phase 1 §3.4): if THIS target's own DSV was
        // created at an earlier size (the target resized since, or it must now also cover a tile / region surface),
        // release + let it recreate lazily on the next stencil scope — done HERE, eagerly, before ANYTHING stamps a
        // fence-gated retirement this frame (PrepareGlyphs' atlas growth, ImageTextureStore.FlushUploads via
        // `_fenceValue + 1`). The wait is THIS target's own in-flight fence value, never a device-wide WaitForGpu.
        int needW = Math.Max((int)sc.W, _stencilFloorW), needH = Math.Max((int)sc.H, _stencilFloorH);
        if (f.StencilDsv != null && StencilDsvPolicy.NeedsRecreate(f.StencilW, f.StencilH, needW, needH))
        {
            WaitForFenceValue(f.LastSubmitFence);
            ReleaseStencilDsv(f);
        }
        // Normally throttle to present cadence before producing this frame. A keep-alive repaint fired from inside an
        // OS modal move/size loop (host called SuppressLatencyWaitOnce) skips it so the WndProc thread isn't blocked
        // up to a vblank — the drag-start/live-resize hitch. Self-resetting: one suppressed wait per call.
        long fenceWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // The render thread normally already paid this wait in TryTakePresentSlot, BEFORE it chose which published
        // frame to present (Terminal/makepad order). The waitable is a semaphore, so waiting twice for one Present
        // would block for a whole extra present cycle: a held credit means "the slot is already ours", so skip. Held on
        // EVERY target, not just the primary: a stood-down / covered / PRESENT_TEST-latched Present spends no credit, so a
        // non-primary (windowed popup, detached pop-out) whose credit was not tracked paid the full 1000 ms bound on its
        // next submit - on the shared render thread, stalling every main-window present behind it.
        bool creditHeld = sc.LatencyCreditHeld;
        bool paidLatencyWait = false;
        if (f.SkipLatencyOnce) f.SkipLatencyOnce = false;
        else if (!creditHeld) { WaitForLatency(sc); paidLatencyWait = true; }
        long latencyDone = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!creditHeld)
            f.LastLatencyWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(fenceWaitStart, latencyDone).TotalMilliseconds;
        f.LastSubmitLatencyWaitMs = paidLatencyWait ? System.Diagnostics.Stopwatch.GetElapsedTime(fenceWaitStart, latencyDone).TotalMilliseconds : 0.0;
        f.FrameIndex = sc.SwapChain->GetCurrentBackBufferIndex();
        int slot = _ring.NextSlot;
        // Ambient copy for nested per-submit helpers (the pass-timeline boundaries, the GPU-timing collectors) that have
        // no `slot` parameter to thread through — valid only inside one submit, like `_w`/`_h`/`_cmdList`.
        _ringSlot = slot;
        // Two waits, both usually no-ops: THIS target's back buffer k and the ring slot (the shared CPU-written banks).
        if (!WaitForFenceValue(f.FenceValues[f.FrameIndex]) || !WaitForFenceValue(_ring.Fence[slot]))
            throw new InvalidOperationException("Frame fence did not reach the awaited value; submit cannot reuse its bank.");
        f.LastFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(fenceWaitStart).TotalMilliseconds;
        f.LastBufferFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(latencyDone).TotalMilliseconds;
        return slot;
    }

    /// <summary>
    /// Open the command list for this submit and record the frame-start work: the retired GPU timings are collected, the
    /// allocator + list reset, the whole-frame timestamp opened, and the uploads recorded — the glyph atlas's dirty band
    /// (DIRECT, bracketed by its own <see cref="GpuPassKind.GlyphBand"/> timestamps), the image copies, one baked-blur job.
    /// </summary>
    private void BeginRecording(D3D12Swapchain sc, TargetFrameState f, int slot, bool isPrimaryTarget)
    {
        EnsureGpuExecutionTiming();
        CollectGpuExecutionTime(slot);   // read this slot's retired always-on whole-frame timestamps
        CollectGpuPassTimeline(slot);    // ...and its retired pass-granular timeline, when one was recorded
        // Measurement knockouts: read ONCE per submit, primary target only (a popup always renders faithfully).
        _frameKnockouts = isPrimaryTarget ? (GpuKnockouts)_knockouts : GpuKnockouts.None;
        GpuDrawCount.Frame = 0;
        _framePassBreaks = 0; _frameBackBufferTransitions = 0;
        _frameImageUploads = 0; _frameUploadBytes = 0; _frameBakedBlurJobs = 0;
        _frameBackBuffer = sc.BackBuffers[f.FrameIndex];
        ID3D12CommandAllocator* allocator = _ring.Allocators[slot];
        Check(allocator->Reset(), "allocator.Reset");
        Check(_cmdList->Reset(allocator, null), "cmdList.Reset");
        Rec(RecordedOp.ListReset, (uint)slot, sc.W << 16 | (sc.H & 0xFFFF), aux: (ushort)f.FrameIndex);
        InvalidateCmdState();   // fresh command list — nothing is bound
        _inRenderPass = false;
        if (_gpuExecutionQueryHeap != null)
            _cmdList->EndQuery(_gpuExecutionQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, 2u * (uint)slot);
        PassBegin(GpuPassKind.Uploads, (int)sc.W, (int)sc.H);
        // FreezeUploads (knockout): leave the glyph band and the image copies queued (HasPendingUploads stays true). The
        // glyph atlas is exempt until its first-ever upload landed: an uninitialized atlas must not be sampled.
        bool freezeUploads = (_frameKnockouts & GpuKnockouts.FreezeUploads) != 0;
        if (!freezeUploads && _imageTextures is { } uploadStore)
        {
            uploadStore.FlushUploads(_cmdList, _fenceValue + 1, _fence->GetCompletedValue());
            _frameImageUploads += uploadStore.LastFlushCopies;
            _frameUploadBytes += uploadStore.LastFlushBytes;
        }
        if (!freezeUploads || !_glyphs!.TextureInitialized)
        {
            PassBoundary(GpuPassKind.GlyphBand, (int)sc.W, (int)sc.H);
            _glyphs!.UploadIfDirty(_cmdList); // the full prepared dirty band, before any glyph draw can sample it
            _frameUploadBytes += _glyphs.LastUploadBytes;
        }
        // One image bake per turn, on its own COMPUTE queue (never this list): recorded, submitted and published behind
        // its fence right here, before the frame's draws — which keep showing the prior pixels until it lands.
        if (isPrimaryTarget &&
            _bakedBlurQueue is { } bakedQueue && _bakedBlur is { } baker && _imageTextures is { } textures
            && baker.DrainOne(textures, bakedQueue))
        {
            Diag.Count("d3d12", "bakedBlurJobs");
            _frameBakedBlurJobs++;
        }
    }

    private void ResetFrameCounters()
    {
        _frameRectCount = 0;
        _frameGlyphInstanceCount = 0;
        _frameImageCount = 0;
        _frameImageSkipped = 0;
        _frameImagesInFlight = 0;
        _framePipeBinds = 0; _framePipeBindsSkipped = 0;
        _frameScissorSets = 0; _frameScissorSkipped = 0;
        _frameSegments = 0; _frameRuns = 0; _frameClipOps = 0; _frameLayerOps = 0; _frameTextCoverFlushes = 0;
        _frameStencilClips = 0; _frameStencilFallback = 0;
        _frameRectOpaqueInsts = 0; _frameRectBlendedInsts = 0;
        _frameRectOpaqueSubmittedPx2 = _frameRectBlendedSubmittedPx2 = 0.0;
        _frameBlendedTopCount = 0; _frameRectAreaOrdinal = 0;
        _cullActive = false;
    }

    // Every pipe banks its instance upload storage by RING SLOT (Phase 1 §3.2/§3.3): the ring's own fence wait just fenced
    // the submit that last used this slot, so writing this bank can never race a still-in-flight GPU read.
    private void BeginPipesFrame(int slot)
    {
        _uploadArena!.BeginFrame(slot);
        _rectPipe!.BeginFrame(slot);
        _shadowPipe!.BeginFrame(slot);
        _arcPipe!.BeginFrame(slot);
        _polylinePipe!.BeginFrame(slot);
        _gradPipe!.BeginFrame(slot);
        _pathPipe!.BeginFrame(slot);
        _seriesPipe!.BeginFrame(slot);
        _spritePipe!.BeginFrame(slot);
        _imagePipe!.BeginFrame(slot);
    }

    /// <summary>
    /// The last half of every submit: close the whole-frame timestamp pair and the pass timeline, close the list (an
    /// always-on forensic line on failure), execute it, signal the frame fence into both ledgers and publish the
    /// submit's device counters.
    /// </summary>
    private void EndRecording(D3D12Swapchain sc, TargetFrameState f, int slot, RepaintRoute route, int backBufferTransitions)
    {
        bool gpuExecutionResolved = false;
        if (_gpuExecutionQueryHeap != null && _gpuExecutionTsReadback != null)
        {
            uint q = 2u * (uint)slot;
            _cmdList->EndQuery(_gpuExecutionQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, q + 1u);
            _cmdList->ResolveQueryData(_gpuExecutionQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP,
                q, 2u, _gpuExecutionTsReadback, (ulong)q * sizeof(ulong));
            Rec(RecordedOp.ResolveQuery, q, 2u);
            gpuExecutionResolved = true;
        }
        PassEnd(sc);
        HRESULT closeHr = _cmdList->Close();
        if ((int)closeHr < 0)
        {
            DrainDebugLayerMessages();
            // ALWAYS-ON (Release too): name the device state and the last recorded state ops before rethrowing — the
            // only evidence a shipping crash leaves. A failed Close leaves the list OPEN, so close it again (the
            // documented recovery) before throwing; never execute it.
            var sb = new System.Text.StringBuilder(2048);
            sb.Append("[d3d12.forensic] cmdList.Close hr=0x").Append(((uint)(int)closeHr).ToString("X8"))
              .Append(" target=").Append(sc.Ordinal).Append(" primary=").Append(ReferenceEquals(sc, _primarySwapchain) ? 1 : 0)
              .Append(" active=").Append(_f?.Target.Ordinal ?? 0)
              .Append(" w=").Append(_w).Append(" h=").Append(_h).Append(" frameIndex=").Append(f.FrameIndex).Append(" slot=").Append(slot)
              .Append(" dsv=").Append(f.StencilW).Append('x').Append(f.StencilH).Append(" depth=").Append(f.StencilDepth)
              .Append(" fence=").Append(_fenceValue).Append(' ');
            _recOps.Format(sb);
            Diag.Line(sb.ToString());
            _ = _cmdList->Close();
            Check(closeHr, "cmdList.Close");
        }
        Rec(RecordedOp.ListClose);

        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, (void**)&execList);   // GEN-COM (wired)
        // One monotonic device fence value, stamped into BOTH ledgers — the target's own back-buffer-keyed FenceValues
        // and the ring's slot-keyed Fence.
        ulong v = SignalNext();
        f.FenceValues[f.FrameIndex] = v;
        f.LastSubmitFence = v;
        ulong gpuExecutionSubmitSequence = sc.NoteGpuSubmit();
        _ring.Stamp(slot, v, sc, gpuExecutionSubmitSequence);
        if (gpuExecutionResolved) _ring.ExecTsPending[slot] = true;
        DrainDebugLayerMessages();
        // Always-on device counters for THIS submit (plain fields — never Diag.*, which compiles out of Release).
        sc.PublishFrameCounters(new GpuFrameCounters(
            gpuExecutionSubmitSequence, route, RepaintFullReason.None, 100f,
            _frameFeatherItems, _surfaces?.ScratchPx ?? 0L, _surfaces?.ScratchLeases ?? 0, _framePassBreaks, GpuDrawCount.Frame, _frameGlyphInstanceCount,
            _frameUploadBytes, _frameImageUploads, _frameBakedBlurJobs, backBufferTransitions,
            route == RepaintRoute.Composite ? _groupRenders : 0, route == RepaintRoute.Composite ? _offGroupHits : 0,
            _surfaces?.RetainedBytes ?? 0L, _frameFeatherPx, route == RepaintRoute.Composite ? _surfaces?.ScratchRefused ?? 0 : 0));
        if (route == RepaintRoute.Composite) NoteScratchRefusedEdge(_surfaces?.ScratchRefused ?? 0);
        sc.PublishRectSubmittedArea(_frameRectOpaqueInsts, _frameRectBlendedInsts, s_rectAreaDiag,
            _frameRectOpaqueSubmittedPx2, _frameRectBlendedSubmittedPx2,
            _frameBlendedTop.AsSpan(0, _frameBlendedTopCount));
        EndTargetFrame();
    }

    // Decode completion → resident GPU texture (media-pipeline §4.1). Staged here (heap/upload only, no command list);
    // the CopyTextureRegion is recorded by FlushUploads at the top of the next SubmitDrawList.
    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
        => _ = TryUploadImage(imageId, pbgra8, w, h);

    public ImageUploadResult TryUploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
        => _imageTextures?.Stage(imageId, pbgra8, w, h) ?? ImageUploadResult.Invalid;

    // Residency evicted the image → free its GPU texture (deferred behind the frame fence in the store).
    public void EvictImage(int imageId) => _imageTextures?.Free(imageId);

    private void ClearInsts() { _rectInsts.Clear(); _glyphInsts.Clear(); _gradGlyphInsts.Clear(); _shadowInsts.Clear(); _arcInsts.Clear(); _polylineInsts.Clear(); _gradInsts.Clear(); _imageDraws.Clear(); _pathDraws.Clear(); _seriesInsts.Clear(); _spriteInsts.Clear(); _runs.Clear(); _pendTextAny = false; }

    // Union a just-appended glyph run's transformed DIP box (plus its cull halo) into the pending-text box.
    private void NotePendingText(float x, float y, float w, float h, float m11, float m12, float m21, float m22, float dx, float dy, float halo)
    {
        RepaintCull.Aabb(x, y, w, h, m11, m12, m21, m22, dx, dy, out float l, out float t, out float r, out float b);
        l -= halo; t -= halo; r += halo; b += halo;
        if (!_pendTextAny) { _pendTextL = l; _pendTextT = t; _pendTextR = r; _pendTextB = b; _pendTextAny = true; return; }
        if (l < _pendTextL) _pendTextL = l;
        if (t < _pendTextT) _pendTextT = t;
        if (r > _pendTextR) _pendTextR = r;
        if (b > _pendTextB) _pendTextB = b;
    }

    // A non-glyph primitive is about to be appended: if it overlaps text already pending in this segment it would be
    // painted UNDER that text by RecordAll's glyph-last order — flush the segment first so stream order is honoured.
    private void CoverPendingText(float x, float y, float w, float h, float m11, float m12, float m21, float m22, float dx, float dy, float halo)
    {
        if (!_pendTextAny) return;
        RepaintCull.Aabb(x, y, w, h, m11, m12, m21, m22, dx, dy, out float l, out float t, out float r, out float b);
        if (r + halo <= _pendTextL || l - halo >= _pendTextR || b + halo <= _pendTextT || t - halo >= _pendTextB) return;
        _frameTextCoverFlushes++;
        FlushSegment(_streamLw, _streamLh);
    }

    // Record (or extend) a painter-order run for the just-appended primitive, so RecordAll can replay in stream order.
    private void PushRun(PrimKind kind, int count = 1)
    {
        if (_blendAdditive)
            kind = kind switch { PrimKind.Rect => PrimKind.RectAdd, PrimKind.Gradient => PrimKind.GradientAdd, PrimKind.Series => PrimKind.SeriesAdd,
                                 PrimKind.Sprites => PrimKind.SpritesAdd, _ => kind };
        int n = _runs.Count;
        if (n > 0 && _runs[n - 1].Kind == kind) _runs[n - 1] = (kind, _runs[n - 1].Count + count);
        else _runs.Add((kind, count));
    }

    private void Decode(ReadOnlySpan<byte> cmds)
    {
        ClearInsts();
        _blendAdditive = false;
        int pos = 0;
        while (pos + sizeof(int) <= cmds.Length) pos = DecodeOne(cmds, pos);
    }

    private int DecodeOne(ReadOnlySpan<byte> cmds, int pos)
    {
        int op = MemoryMarshal.Read<int>(cmds.Slice(pos));
        pos += sizeof(int);
        switch ((DrawOp)op)
        {
                case DrawOp.FillRoundRect:
                {
                    var c = MemoryMarshal.Read<FillRoundRectCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<FillRoundRectCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    var inst = new RectInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        RTL = c.Radii.TopLeft, RTR = c.Radii.TopRight, RBR = c.Radii.BottomRight, RBL = c.Radii.BottomLeft,
                        R = c.Fill.R, G = c.Fill.G, B = c.Fill.B, A = c.Fill.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity,
                        Kind = c.FillKind, CellPx = c.CellPx,
                        BR = c.ColorB.R, BG = c.ColorB.G, BB = c.ColorB.B, BA = c.ColorB.A,
                    };
                    ApplyRoundedClip(ref inst);
                    _rectInsts.Add(inst);
                    _frameRectCount++;
                    PushRun(PrimKind.Rect);
                    break;
                }
                case DrawOp.DrawGlyphRun:
                {
                    var g = MemoryMarshal.Read<DrawGlyphRunCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGlyphRunCmd>();
                    if (Cull(g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Bounds.H, g.Transform.M11, g.Transform.M12,
                             g.Transform.M21, g.Transform.M22, g.Transform.Dx, g.Transform.Dy,
                             RepaintCull.GlyphHalo(g.FontSize))) break;
                    string s = _strings.Resolve(g.Text);
                    // A non-empty id resolving to "" is a run whose interned text was reclaimed while its command bytes
                    // still reference it (a reused span outliving StringTable's quarantine) — silently skipped otherwise.
                    if (s.Length == 0 && !g.Text.IsEmpty) Diag.Count("d3d12", "glyphRunEmptyResolve");
                    if (s.Length > 0)
                    {
                        int before = _glyphInsts.Count;
                        _glyphs!.LayoutRun(g.Text, g.Family, s, _strings.Resolve(g.Family), g.FontSize, g.Weight, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines,
                            g.CharSpacing, g.LineHeight, g.LineStacking, g.LineBounds, g.Color, _frameScale, g.Transform, g.Opacity, _glyphInsts,
                            g.SpanRunId, g.ForceColor != 0, g.InMotion * (1f / 255f));
                        _frameGlyphInstanceCount += _glyphInsts.Count - before;
                        NoteGlyphHaloCoverage(before, g.Bounds, RepaintCull.GlyphHalo(g.FontSize));
                        if (_glyphInsts.Count > before)
                            NotePendingText(g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Bounds.H, g.Transform.M11, g.Transform.M12,
                                g.Transform.M21, g.Transform.M22, g.Transform.Dx, g.Transform.Dy, RepaintCull.GlyphHalo(g.FontSize));
                    }
                    break;
                }
                case DrawOp.DrawGlyphRunGradient:
                {
                    var g = MemoryMarshal.Read<DrawGlyphRunGradientCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>();
                    if (Cull(g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Bounds.H, g.Transform.M11, g.Transform.M12,
                             g.Transform.M21, g.Transform.M22, g.Transform.Dx, g.Transform.Dy,
                             RepaintCull.GlyphHalo(g.FontSize, g.Lift))) break;
                    string s = _strings.Resolve(g.Text);
                    if (s.Length > 0)
                    {
                        int beforeGrad = _gradGlyphInsts.Count, beforePlain = _glyphInsts.Count;
                        // Karaoke wipe: a per-PIXEL sub-glyph gradient (before→after over a soft band at the split) via a
                        // SEPARATE gradient PSO/batch (_gradGlyphInsts) — normal text keeps its lean single-color path.
                        // A SETTLED wipe (split ≤ 0 or ≥ 1) has a constant fill, so LayoutRunGradient routes it into the
                        // plain batch instead (pixel-identical — see its settled-split fast path); hence both lists here.
                        _glyphs!.LayoutRunGradient(g.Text, g.Family, s, _strings.Resolve(g.Family), g.FontSize, g.Weight, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines,
                            g.CharSpacing, g.LineHeight, g.LineStacking, g.LineBounds, g.Before, g.After, g.Split, g.Softness, g.Lift, _frameScale, g.Transform, g.Opacity,
                            _gradGlyphInsts, _glyphInsts,
                            g.SpanRunId, g.InMotion * (1f / 255f));
                        _frameGlyphInstanceCount += (_gradGlyphInsts.Count - beforeGrad) + (_glyphInsts.Count - beforePlain);
                        NoteGlyphHaloCoverage(beforePlain, g.Bounds, RepaintCull.GlyphHalo(g.FontSize, g.Lift));
                        if (_gradGlyphInsts.Count > beforeGrad || _glyphInsts.Count > beforePlain)
                            NotePendingText(g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Bounds.H, g.Transform.M11, g.Transform.M12,
                                g.Transform.M21, g.Transform.M22, g.Transform.Dx, g.Transform.Dy, RepaintCull.GlyphHalo(g.FontSize, g.Lift));
                    }
                    break;
                }
                case DrawOp.PushClip:
                    // Tier-1 scissor clip. The headless path is the verified source of truth for clip *semantics*;
                    // wiring it onto the GPU (RSSetScissorRects between clip-broken batches, or a per-instance
                    // shader discard) is the needs-pixels follow-up (BUILD-ROADMAP step 27). For now D3D12 consumes
                    // the opcode so the stream stays well-formed and rendering is unchanged (nothing overdraws yet
                    // because scroll content is bounded by layout). TODO(step-27): apply the scissor.
                    pos += Unsafe.SizeOf<ClipCmd>();
                    break;
                case DrawOp.PopClip:
                    break;
                // Tier-3 stencil path clip: both live WALKS intercept these BEFORE DecodeOne (they are scope ops, not
                // primitives). This arm exists only so the legacy whole-stream Decode() path stays byte-aligned.
                case DrawOp.PushStencilClip:
                    pos += Unsafe.SizeOf<PushStencilClipCmd>();
                    break;
                case DrawOp.PopStencilClip:
                    pos += Unsafe.SizeOf<PopStencilClipCmd>();
                    break;
                case DrawOp.DrawImage:
                {
                    var im = MemoryMarshal.Read<DrawImageCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawImageCmd>();
                    if (Cull(im.Rect.X, im.Rect.Y, im.Rect.W, im.Rect.H, im.Transform.M11, im.Transform.M12,
                             im.Transform.M21, im.Transform.M22, im.Transform.Dx, im.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(im.Rect.X, im.Rect.Y, im.Rect.W, im.Rect.H, im.Transform.M11, im.Transform.M12,
                             im.Transform.M21, im.Transform.M22, im.Transform.Dx, im.Transform.Dy, RepaintCull.AaHaloDip);
                    if (_imgRecSlot >= 0) NoteRasterImage(im.ImageId);   // a tile raster records which pixels of the id it drew
                    // Draw whatever texture is resident under this id — the BlurHash LQIP preview (uploaded at request)
                    // OR the full-res art (which replaces it on decode). Flat tint only when no texture exists yet.
                    if (_imageTextures!.IsResident(im.ImageId)) AddReadyImage(in im);
                    else
                    {
                        // Pixels staged or still on the copy / compute queue: the placeholder stands in, and a retained
                        // tile holding it is not faithful (it re-rasters once they land — RasterTiles).
                        if (_imageTextures.IsInFlight(im.ImageId)) _frameImagesInFlight++;
                        AddImagePlaceholder(in im);
                    }
                    break;
                }
                case DrawOp.DrawRoundRectStroke:
                {
                    var c = MemoryMarshal.Read<DrawRoundRectStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.StrokeWidth))) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.StrokeWidth));
                    var inst = new RectInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        RTL = c.Radii.TopLeft, RTR = c.Radii.TopRight, RBR = c.Radii.BottomRight, RBL = c.Radii.BottomLeft,
                        R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, StrokeWidth = c.StrokeWidth,
                        DashOn = c.DashOn, DashOff = c.DashOff,   // 0/0 = solid (the shader gates on BOTH > 0)
                    };
                    ApplyRoundedClip(ref inst);
                    _rectInsts.Add(inst);
                    _frameRectCount++;
                    PushRun(PrimKind.Rect);
                    break;
                }
                case DrawOp.DrawTabShape:
                {
                    // WinUI selected-tab shape (TabViewItem.cpp:98-123) — a RoundRect-pipeline SDF variant:
                    // radii.x carries the top radius, radii.w (RBL) the bottom flare radius (see RectInstance docs).
                    var c = MemoryMarshal.Read<DrawTabShapeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawTabShapeCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    var inst = new RectInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        RTL = c.TopRadius, RBL = c.FlareRadius,
                        R = c.Fill.R, G = c.Fill.G, B = c.Fill.B, A = c.Fill.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity,
                        Kind = 2f,
                    };
                    ApplyRoundedClip(ref inst);
                    _rectInsts.Add(inst);
                    _frameRectCount++;
                    PushRun(PrimKind.Rect);
                    break;
                }
                case DrawOp.DrawShadow:
                {
                    var c = MemoryMarshal.Read<DrawShadowCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawShadowCmd>();
                    // The shadow quad is the caster box TRANSLATED by (OffsetX, OffsetY) and inflated by spread + the
                    // gaussian tail — shift the box, then use the symmetric halo (ShadowPipeline VS).
                    if (Cull(c.Rect.X + c.OffsetX, c.Rect.Y + c.OffsetY, c.Rect.W, c.Rect.H,
                             c.Transform.M11, c.Transform.M12, c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.ShadowHalo(c.Spread, c.Blur))) break;
                    CoverPendingText(c.Rect.X + c.OffsetX, c.Rect.Y + c.OffsetY, c.Rect.W, c.Rect.H,
                             c.Transform.M11, c.Transform.M12, c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.ShadowHalo(c.Spread, c.Blur));
                    _shadowInsts.Add(new ShadowInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Radius = c.Radii.TopLeft, Opacity = c.Opacity,
                        Blur = c.Blur, Spread = c.Spread, OffX = c.OffsetX, OffY = c.OffsetY,
                    });
                    PushRun(PrimKind.Shadow);
                    break;
                }
                case DrawOp.DrawArc:
                {
                    var c = MemoryMarshal.Read<DrawArcCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawArcCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.Thickness))) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.Thickness));
                    const float Deg2Rad = MathF.PI / 180f;
                    _arcInsts.Add(new ArcInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Thickness = c.Thickness, Opacity = c.Opacity,
                        StartRad = c.StartDeg * Deg2Rad, SweepRad = c.SweepDeg * Deg2Rad, RoundCaps = c.RoundCaps != 0 ? 1f : 0f,
                    });
                    PushRun(PrimKind.Arc);
                    break;
                }
                case DrawOp.DrawPolylineStroke:
                {
                    var c = MemoryMarshal.Read<DrawPolylineStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawPolylineStrokeCmd>();
                    // PolylineStrokePipeline's VS rasterizes exactly Rect ± (thickness/2 + 2) — the points are Rect-local.
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.Thickness))) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.Thickness));
                    _polylineInsts.Add(new PolylineStrokeInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Thickness = c.Thickness, Opacity = c.Opacity,
                        P0X = c.P0.X, P0Y = c.P0.Y, P1X = c.P1.X, P1Y = c.P1.Y,
                        P2X = c.P2.X, P2Y = c.P2.Y, P3X = c.P3.X, P3Y = c.P3.Y,
                        PointCount = c.PointCount, TrimStart = c.TrimStart, TrimEnd = c.TrimEnd, RoundCaps = c.RoundCaps != 0 ? 1f : 0f,
                    });
                    PushRun(PrimKind.Polyline);
                    break;
                }
                case DrawOp.DrawSprites:
                {
                    var c = MemoryMarshal.Read<DrawSpritesCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawSpritesCmd>();
                    float halo = RepaintCull.AaHaloDip + 1f;   // the kernel's one-DIP AA pad
                    if (c.Count <= 0 || Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo);
                    int count = Math.Min(c.Count, 16);
                    for (int i = 0; i < count; i++) _spriteInsts.Add(SpriteInstance.From(in c.S[i], in c));
                    PushRun(PrimKind.Sprites, count);
                    break;
                }
                case DrawOp.SetBlend:
                {
                    _blendAdditive = _blendBase || MemoryMarshal.Read<SetBlendCmd>(cmds.Slice(pos)).Mode == (int)PaintBlend.Additive;
                    pos += Unsafe.SizeOf<SetBlendCmd>();
                    break;
                }
                case DrawOp.DrawSeries:
                {
                    var c = MemoryMarshal.Read<DrawSeriesCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawSeriesCmd>();
                    // SeriesPipeline's VS clamps every vertex's y to Rect.Y..Rect.Y+H, so the box is the cull rect; a Stroke
                    // ribbon's normal can push x past it by thickness/2, which StrokeHalo covers.
                    float halo = c.Shape >= 2 ? RepaintCull.StrokeHalo(c.Thickness + 2f) : RepaintCull.AaHaloDip;   // Stroke and Polar ribbons: + the 1-DIP AA fringe each side
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo);
                    _seriesInsts.Add(SeriesInstance.From(in c));
                    PushRun(PrimKind.Series);
                    break;
                }
                case DrawOp.DrawGradientRect:
                {
                    var c = MemoryMarshal.Read<DrawGradientRectCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGradientRectCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    var inst = new GradientInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        StartX = c.Start.X, StartY = c.Start.Y, EndX = c.End.X, EndY = c.End.Y,
                        C0R = c.C0.R, C0G = c.C0.G, C0B = c.C0.B, C0A = c.C0.A,
                        C1R = c.C1.R, C1G = c.C1.G, C1B = c.C1.B, C1A = c.C1.A,
                        C2R = c.C2.R, C2G = c.C2.G, C2B = c.C2.B, C2A = c.C2.A,
                        C3R = c.C3.R, C3G = c.C3.G, C3B = c.C3.B, C3A = c.C3.A,
                        O0 = c.O0, O1 = c.O1, O2 = c.O2, O3 = c.O3,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Radius = c.Radii.TopLeft, Opacity = c.Opacity,
                        Shape = c.Shape, StopCount = c.StopCount,
                    };
                    ApplyRoundedClip(ref inst);
                    _gradInsts.Add(inst);
                    PushRun(PrimKind.Gradient);
                    break;
                }
                case DrawOp.DrawGradientStroke:
                {
                    var c = MemoryMarshal.Read<DrawGradientStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGradientStrokeCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.StrokeWidth))) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy,
                             RepaintCull.StrokeHalo(c.StrokeWidth));
                    var inst = new GradientInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        StartX = c.Start.X, StartY = c.Start.Y, EndX = c.End.X, EndY = c.End.Y,
                        C0R = c.C0.R, C0G = c.C0.G, C0B = c.C0.B, C0A = c.C0.A,
                        C1R = c.C1.R, C1G = c.C1.G, C1B = c.C1.B, C1A = c.C1.A,
                        C2R = c.C2.R, C2G = c.C2.G, C2B = c.C2.B, C2A = c.C2.A,
                        C3R = c.C3.R, C3G = c.C3.G, C3B = c.C3.B, C3A = c.C3.A,
                        O0 = c.O0, O1 = c.O1, O2 = c.O2, O3 = c.O3,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Radius = c.Radii.TopLeft, Opacity = c.Opacity,
                        Shape = c.Shape, StopCount = c.StopCount, Stroke = c.StrokeWidth,
                    };
                    ApplyRoundedClip(ref inst);
                    _gradInsts.Add(inst);
                    PushRun(PrimKind.Gradient);
                    break;
                }
                case DrawOp.DrawIconMask:
                {
                    var ic = MemoryMarshal.Read<DrawIconMaskCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawIconMaskCmd>();
                    if (Cull(ic.Rect.X, ic.Rect.Y, ic.Rect.W, ic.Rect.H, ic.Transform.M11, ic.Transform.M12,
                             ic.Transform.M21, ic.Transform.M22, ic.Transform.Dx, ic.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(ic.Rect.X, ic.Rect.Y, ic.Rect.W, ic.Rect.H, ic.Transform.M11, ic.Transform.M12,
                             ic.Transform.M21, ic.Transform.M22, ic.Transform.Dx, ic.Transform.Dy, RepaintCull.AaHaloDip);
                    if (ic.PathId != 0 && ic.Tint.A > 0f && ic.Rect.W > 0f && ic.Rect.H > 0f)
                    {
                        // Device px like glyphs (size × dpi). Miss ⇒ rasterize now (colorless R8) + shelf-pack into the
                        // glyph atlas; then append ONE tinted glyph instance so it batches in the glyph pass.
                        int wPx = Math.Max(1, (int)MathF.Round(ic.Rect.W * _frameScale));
                        int hPx = Math.Max(1, (int)MathF.Round(ic.Rect.H * _frameScale));
                        if (!_glyphs!.TryGetIconUv(ic.PathId, wPx, hPx, out float u0, out float v0, out float u1, out float v1))
                        {
                            int need = wPx * hPx;
                            byte[] buf = ArrayPool<byte>.Shared.Rent(need);
                            IconGeometryTable.Shared.Rasterize(ic.PathId, wPx, hPx, buf.AsSpan(0, need));
                            _glyphs.PackIconMask(ic.PathId, wPx, hPx, buf, out u0, out v0, out u1, out v1);
                            ArrayPool<byte>.Shared.Return(buf);
                        }
                        if (u1 > u0 && v1 > v0)
                        {
                            _glyphInsts.Add(new GlyphInstance
                            {
                                DstX = ic.Rect.X, DstY = ic.Rect.Y, DstW = ic.Rect.W, DstH = ic.Rect.H,
                                U0 = u0, V0 = v0, U1 = u1, V1 = v1,
                                R = ic.Tint.R, G = ic.Tint.G, B = ic.Tint.B, A = ic.Tint.A,
                                M11 = ic.Transform.M11, M12 = ic.Transform.M12, M21 = ic.Transform.M21, M22 = ic.Transform.M22,
                                Dx = ic.Transform.Dx, Dy = ic.Transform.Dy, Opacity = ic.Opacity,
                            });
                            _frameGlyphInstanceCount++;
                        }
                    }
                    break;
                }
                case DrawOp.DrawVideo:
                {
                    // The video hole punch: the SAME RectInstance/shader as a fill, drawn later through the DestOut PSO.
                    // Color is (0,0,0,VideoReady) and Kind stays 0 (a rounded-rect SDF — Kind 3 would fall into the
                    // tab-shape branch), so the PS emits premultiplied (0,0,0, VideoReady·cov·opacity) and the blend
                    // leaves dst×(1−VideoReady·cov·opacity): corner AA, per-corner radii and the tier-2 rounded clip
                    // (the source of the rounded PiP hole) all come from the existing paths.
                    var c = MemoryMarshal.Read<DrawVideoCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawVideoCmd>();
                    if (c.VideoReady <= 0f || c.Opacity <= 0f) break;   // nothing to erase (poster/audio-only)
                    // A hole punch that misses this replay's target (another tile) is culled like any primitive.
                    if (Cull(c.Dst.X, c.Dst.Y, c.Dst.W, c.Dst.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Dst.X, c.Dst.Y, c.Dst.W, c.Dst.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    var inst = new RectInstance
                    {
                        PosX = c.Dst.X, PosY = c.Dst.Y, W = c.Dst.W, H = c.Dst.H,
                        RTL = c.Radii.TopLeft, RTR = c.Radii.TopRight, RBR = c.Radii.BottomRight, RBL = c.Radii.BottomLeft,
                        R = 0f, G = 0f, B = 0f, A = c.VideoReady,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity,
                    };
                    ApplyRoundedClip(ref inst);
                    _rectInsts.Add(inst);
                    _frameRectCount++;
                    PushRun(PrimKind.VideoHole);
                    break;
                }
                case DrawOp.EraseRoundRect:
                {
                    // The general rounded-rect erase — the SAME instance/shader/DestOut PSO as the video hole punch
                    // above, minus the surface identity. Colour (0,0,0,Strength) with Kind 0 (rounded-rect SDF) so the
                    // PS emits premultiplied (0,0,0, Strength*cov*opacity) and the blend leaves dst x (1 - that): the
                    // scrim's cutouts get corner AA, per-corner radii and the tier-2 rounded clip for free. Bound
                    // surface = whatever is active, i.e. the opacity-group RT for the scrim band.
                    var e = MemoryMarshal.Read<EraseRoundRectCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<EraseRoundRectCmd>();
                    if (e.Strength <= 0f || e.Opacity <= 0f) break;   // nothing to erase
                    if (Cull(e.Rect.X, e.Rect.Y, e.Rect.W, e.Rect.H, e.Transform.M11, e.Transform.M12,
                             e.Transform.M21, e.Transform.M22, e.Transform.Dx, e.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(e.Rect.X, e.Rect.Y, e.Rect.W, e.Rect.H, e.Transform.M11, e.Transform.M12,
                             e.Transform.M21, e.Transform.M22, e.Transform.Dx, e.Transform.Dy, RepaintCull.AaHaloDip);
                    var einst = new RectInstance
                    {
                        PosX = e.Rect.X, PosY = e.Rect.Y, W = e.Rect.W, H = e.Rect.H,
                        RTL = e.Radii.TopLeft, RTR = e.Radii.TopRight, RBR = e.Radii.BottomRight, RBL = e.Radii.BottomLeft,
                        R = 0f, G = 0f, B = 0f, A = e.Strength,
                        M11 = e.Transform.M11, M12 = e.Transform.M12, M21 = e.Transform.M21, M22 = e.Transform.M22,
                        Dx = e.Transform.Dx, Dy = e.Transform.Dy, Opacity = e.Opacity,
                    };
                    ApplyRoundedClip(ref einst);
                    _rectInsts.Add(einst);
                    _frameRectCount++;
                    PushRun(PrimKind.VideoHole);
                    break;
                }
                case DrawOp.PushLayer:
                {
                    // This route composites NO layers: it is a NON-PRIMARY swapchain (a popup window, a detached pop-out)
                    // with no retained composite behind it. An Acrylic layer's opaque FallbackColor base — the first
                    // thing the composite's backdrop pass lays down under the blur — is therefore never produced here, and the recorder
                    // has already dropped the node's duplicate Fallback fill (it would occlude the frost wherever the
                    // layer DOES run). Paint the fallback in its place: WinUI's own no-transparency answer for the
                    // surface, instead of a see-through plate of floating text. Rect + radii are already device-space,
                    // so the transform is identity.
                    var L = MemoryMarshal.Read<PushLayerCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<PushLayerCmd>();
                    if (L.Kind != (int)LayerKind.Acrylic || L.Fallback.A <= 0f || L.GroupAlpha <= 0f) break;
                    if (Cull(L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H, 1f, 0f, 0f, 1f, 0f, 0f, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H, 1f, 0f, 0f, 1f, 0f, 0f, RepaintCull.AaHaloDip);
                    var linst = new RectInstance
                    {
                        PosX = L.DeviceRect.X, PosY = L.DeviceRect.Y, W = L.DeviceRect.W, H = L.DeviceRect.H,
                        RTL = L.Radii.TopLeft, RTR = L.Radii.TopRight, RBR = L.Radii.BottomRight, RBL = L.Radii.BottomLeft,
                        R = L.Fallback.R, G = L.Fallback.G, B = L.Fallback.B, A = L.Fallback.A,
                        M11 = 1f, M12 = 0f, M21 = 0f, M22 = 1f, Dx = 0f, Dy = 0f, Opacity = L.GroupAlpha,
                    };
                    ApplyRoundedClip(ref linst);
                    _rectInsts.Add(linst);
                    _frameRectCount++;
                    PushRun(PrimKind.Rect);
                    break;
                }
                case DrawOp.PopLayer:
                    pos += Unsafe.SizeOf<PopLayerCmd>();
                    break;
                case DrawOp.FillPath:
                {
                    var c = MemoryMarshal.Read<FillPathCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<FillPathCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    if (c.VtxCount > 0 && c.IdxCount > 0)
                    {
                        _pathDraws.Add(new PathDrawItem
                        {
                            VtxStart = c.VtxStart, VtxCount = c.VtxCount, IdxStart = c.IdxStart, IdxCount = c.IdxCount,
                            Inst = new PathInstance
                            {
                                R = c.Fill.R, G = c.Fill.G, B = c.Fill.B, A = c.Fill.A,
                                M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                                Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, ArcLenPx = 0f,
                                // Fills pass the full-cover trim window and no dash — a per-draw UNIFORM, never geometry —
                                // so the SAME PathPipeline shader/PSO serves both DrawOp.FillPath and DrawOp.StrokePath.
                                TrimStart = 0f, TrimEnd = 1f, DashOn = 0f, DashOff = 0f,
                            },
                        });
                        PushRun(PrimKind.Path);
                    }
                    break;
                }
                case DrawOp.StrokePath:
                {
                    var c = MemoryMarshal.Read<StrokePathCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<StrokePathCmd>();
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, RepaintCull.AaHaloDip);
                    if (c.VtxCount > 0 && c.IdxCount > 0)
                    {
                        _pathDraws.Add(new PathDrawItem
                        {
                            VtxStart = c.VtxStart, VtxCount = c.VtxCount, IdxStart = c.IdxStart, IdxCount = c.IdxCount,
                            Inst = new PathInstance
                            {
                                R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                                M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                                Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, ArcLenPx = c.ArcLenPx,
                                TrimStart = c.TrimStart, TrimEnd = c.TrimEnd, DashOn = c.DashOn, DashOff = c.DashOff,
                            },
                        });
                        PushRun(PrimKind.Path);
                    }
                    break;
                }
                default:
                    pos = cmds.Length;
                    break;
        }
        return pos;
    }

    private void AddImagePlaceholder(in DrawImageCmd im)
    {
        var inst = new RectInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            RTL = im.Radii.TopLeft, RTR = im.Radii.TopRight, RBR = im.Radii.BottomRight, RBL = im.Radii.BottomLeft,
            R = im.Placeholder.R, G = im.Placeholder.G, B = im.Placeholder.B, A = im.Placeholder.A,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy, Opacity = im.Opacity,
        };
        ApplyRoundedClip(ref inst);
        _rectInsts.Add(inst);
        _frameRectCount++;
        PushRun(PrimKind.Rect);
    }

    // Ready image: sample the resident GPU texture through the ImagePipeline. It draws ABOVE the card/section
    // background rects but BELOW glyph labels (see RecordAll ordering). CrossFade=1 ⇒ full image; the placeholder→image
    // fade (M4) just drives CrossFade 0→1 — the shader already lerps placeholder→sampled in premultiplied space.
    private void AddReadyImage(in DrawImageCmd im)
    {
        float crossFade = ImageCache.ResolveFade(_imageClockMs, im.FadeStartMs, im.FadeDurationMs, im.FadeEasing);
        var inst = new ImageInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            RTL = im.Radii.TopLeft, RTR = im.Radii.TopRight, RBR = im.Radii.BottomRight, RBL = im.Radii.BottomLeft,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy,
            Opacity = im.Opacity, CrossFade = crossFade,
            PR = im.Placeholder.R, PG = im.Placeholder.G, PB = im.Placeholder.B, PA = im.Placeholder.A,
            UvX = im.UvRect.X, UvY = im.UvRect.Y, UvW = im.UvRect.W, UvH = im.UvRect.H,
            OverlayR = im.Overlay.R, OverlayG = im.Overlay.G, OverlayB = im.Overlay.B, OverlayA = im.Overlay.A,
            MaskEdges = im.MaskEdges, MaskLeft = im.MaskLeft, MaskTop = im.MaskTop,
            MaskRight = im.MaskRight, MaskBottom = im.MaskBottom,
            MaskFalloff = im.MaskFalloff, MaskIntensity = im.MaskIntensity,
            Saturation = im.Saturation,
        };
        ApplyRoundedClip(ref inst);
        _imageDraws.Add((inst, im.ImageId));
        _frameImageCount++;
        PushRun(PrimKind.Image);
    }

    private void SetFullViewport()
    {
        _targetOriginX = 0; _targetOriginY = 0;
        _targetWidth = (int)_w; _targetHeight = (int)_h;
        D3D12_VIEWPORT vpd = new() { TopLeftX = 0, TopLeftY = 0, Width = _w, Height = _h, MinDepth = 0, MaxDepth = 1 };
        _cmdList->RSSetViewports(1, &vpd);
        RecCoalesced(RecordedOp.Viewport, _w, _h);
        SetFullScissor();
    }

    // The frame's OUTERMOST scissor: the whole current target, expressed in the replay's pixel space (a target whose
    // origin is shifted — a tile, a region scratch — sees its own extent).
    private RECT FullScissorRect()
        => new RECT { left = _targetOriginX, top = _targetOriginY, right = _targetOriginX + TargetW, bottom = _targetOriginY + TargetH };

    private void SetFullScissor()
        => SetScissorRect(FullScissorRect());

    // Single scissor chokepoint with dedup: an unchanged rect records nothing (valid because RSSetScissorRects is pure
    // command-list state — only invalidated when a compositor pass sets its own scissor, via InvalidateCmdState).
    private void SetScissorRect(RECT sc)
    {
        sc = ClampToReplay(sc);
        int tw = _targetWidth > 0 ? _targetWidth : (int)_w;
        int th = _targetHeight > 0 ? _targetHeight : (int)_h;
        RECT targetScissor = new()
        {
            left = Math.Clamp(sc.left - _targetOriginX, 0, tw),
            top = Math.Clamp(sc.top - _targetOriginY, 0, th),
            right = Math.Clamp(sc.right - _targetOriginX, 0, tw),
            bottom = Math.Clamp(sc.bottom - _targetOriginY, 0, th),
        };
        if (targetScissor.right < targetScissor.left) targetScissor.right = targetScissor.left;
        if (targetScissor.bottom < targetScissor.top) targetScissor.bottom = targetScissor.top;
        if (_scissorValid && targetScissor.left == _lastScissor.left && targetScissor.top == _lastScissor.top
            && targetScissor.right == _lastScissor.right && targetScissor.bottom == _lastScissor.bottom) { _frameScissorSkipped++; return; }
        _lastScissor = targetScissor;
        _scissorValid = true;
        _frameScissorSets++;
        _cmdList->RSSetScissorRects(1, &targetScissor);
        RecCoalesced(RecordedOp.Scissor, (uint)targetScissor.left << 16 | ((uint)targetScissor.top & 0xFFFF),
            (uint)targetScissor.right << 16 | ((uint)targetScissor.bottom & 0xFFFF));
    }

    // DIP → device pixels, rounding OUT. Delegates to RepaintPolicy.ToPixel so the scissor, the partial frame's per-rect
    // CLEAR list and the decode-time cull rect are all the same arithmetic by construction, not by three copies of it.
    private RECT ToScissor(in RectF r)
    {
        // DIP → device px rounding OUT (RepaintPolicy.ToPixel is the one arithmetic), in the replay's pixel space; the
        // chokepoint clamps to the target.
        float s = _frameScale <= 0f ? 1f : _frameScale;
        if (r.IsEmpty) return default;
        int l = (int)MathF.Floor(r.X * s), t = (int)MathF.Floor(r.Y * s);
        int rr = (int)MathF.Ceiling(r.Right * s), b = (int)MathF.Ceiling(r.Bottom * s);
        const int lim = 1 << 24;
        return new RECT { left = Math.Clamp(l, -lim, lim), top = Math.Clamp(t, -lim, lim), right = Math.Clamp(rr, -lim, lim), bottom = Math.Clamp(b, -lim, lim) };
    }

    private static RECT ToRect(in FluentGpu.Rhi.PixelRect p)
        => new() { left = p.Left, top = p.Top, right = p.Right, bottom = p.Bottom };

    private void SetScissor(in RectF r) => SetScissorRect(ToScissor(r));

    private static bool SameRect(in RECT a, in RECT b)
        => a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

    // The innermost clip (the whole target when none is open).
    private RECT CurrentScissorRect()
        => _clipStack.Count == 0 ? FullScissorRect() : ToScissor(_clipStack[^1]);

    /// <summary>
    /// I4 — the glyph cull halo's COUPLING CHECK, and the only thing that makes it a bound rather than a guess. The five
    /// geometric halos are re-derivable from their vertex shaders; a glyph run's is not (<c>GlyphRenderer</c> places
    /// quads from shaped advances + atlas bearings + baseline snapping, against a declared <c>Bounds</c> that is the NODE
    /// BOX). So measure it: every quad this run emitted must lie inside <c>Bounds</c> inflated by the halo the cull uses,
    /// or a run straddling a replay rect could be dropped and leave a chopped half-letter frozen in the canvas.
    /// <para>Breaches accumulate into <c>glyphHaloBreach</c> — a nonzero reading names an under-covering class (oversized
    /// colour-emoji fallback, a tight LineStacking/LineBounds line box, a trimmed measurement the shaped run overflows).
    /// Const-gated to DEBUG / FLUENTGPU_DIAG, so the shipping AOT binary pays nothing.</para>
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("FLUENTGPU_DIAG")]
    private void NoteGlyphHaloCoverage(int firstInstance, in RectF bounds, float halo)
    {
        // Instance Dst is in the SAME local space as the run's declared Bounds (LayoutRun is handed Bounds.X/Y as its
        // origin and applies the world transform per instance), so the containment test is a plain rect compare.
        float l = bounds.X - halo, t = bounds.Y - halo, r = bounds.Right + halo, b = bounds.Bottom + halo;
        for (int i = firstInstance; i < _glyphInsts.Count; i++)
        {
            var gi = _glyphInsts[i];
            if (gi.DstX >= l && gi.DstY >= t && gi.DstX + gi.DstW <= r && gi.DstY + gi.DstH <= b) continue;
            _glyphHaloBreaches++;
            Diag.Set("d3d12", "glyphHaloBreach", _glyphHaloBreaches);
        }
    }

    private long _glyphHaloBreaches;

    /// <summary>Glyph quads observed OUTSIDE the cull halo since process start (DEBUG / FLUENTGPU_DIAG only; always 0 in
    /// a Release build). Nonzero ⇒ <see cref="RepaintCull.GlyphHalo"/> can under-cover and drop a boundary run.</summary>
    public long GlyphHaloBreaches => _glyphHaloBreaches;

    /// <summary>Decode-time cull test: true ⇒ SKIP this primitive — its conservative device AABB (inflated by the per-kind
    /// <paramref name="halo"/>) misses the replay's target. Always false outside a culled replay.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Cull(float x, float y, float w, float h,
        float m11, float m12, float m21, float m22, float dx, float dy, float halo)
    {
        if (!_cullActive) return false;
        RepaintCull.Aabb(x, y, w, h, m11, m12, m21, m22, dx, dy, out float l, out float t, out float r, out float b);
        return !RepaintCull.Keep(l, t, r, b, halo, in _cullRect);
    }

    private bool HasPendingSegment()
        => _runs.Count != 0 || _glyphInsts.Count != 0 || _gradGlyphInsts.Count != 0;

    private void ResetDesiredScissor()
    {
        _desiredScissor = CurrentScissorRect();
        _desiredScissorValid = true;
        SetScissorRect(_desiredScissor);
    }

    private void EnsureDesiredScissor(in RECT next, float lw, float lh)
    {
        if (_desiredScissorValid && SameRect(in _desiredScissor, in next)) return;
        if (HasPendingSegment()) FlushSegment(lw, lh);
        _desiredScissor = next;
        _desiredScissorValid = true;
    }

    private void PushScissor(in ClipCmd clip)
    {
        _clipStack.Add(clip.DeviceRect);
        // Rounded entry rides a parallel stack: a tier-2 rounded PushClip replaces the active rounded clip; a plain
        // rectangular PushClip INHERITS the enclosing one, so content nested under a rounded surface keeps the
        // surface's corner clamp while the scissor narrows.
        if (clip.CornerRadius > 0f) _roundedClipStack.Add((clip.RoundedRect, clip.CornerRadius));
        else if (_roundedClipStack.Count > 0) _roundedClipStack.Add(_roundedClipStack[^1]);
        else _roundedClipStack.Add((default, 0f));
    }

    private void PopScissor()
    {
        if (_clipStack.Count > 0) _clipStack.RemoveAt(_clipStack.Count - 1);
        if (_roundedClipStack.Count > 0) _roundedClipStack.RemoveAt(_roundedClipStack.Count - 1);
    }

    // ── Tier-3 stencil path clip: the D3D12 sub-protocol (gpu-renderer.md §6) ────────────────────────────────────────
    // A scope is: FlushSegment (the non-reorderable pass boundary S3.3 rule 5) → push DeviceRect as a plain scissor →
    // clear this rect's stencil on the OUTERMOST push → attach the DSV → draw the mask with INCR_SAT → bump the ref.
    // Draws until the matching pop bind a StencilFunc = EQUAL clone of their pipeline. The pop re-carries the push's
    // realization byte-identically, so an INNER pop erases its own level with DECR_SAT and no geometry stack exists.

    private int TargetW => _targetWidth > 0 ? _targetWidth : (int)_w;
    private int TargetH => _targetHeight > 0 ? _targetHeight : (int)_h;

    /// <summary>A stencil scope can mask on the current target when the target's DSV covers it (the DSV is created at
    /// max(window, tile) — <see cref="_stencilFloorW"/>/<see cref="_stencilFloorH"/>); a larger target (an inline group's
    /// halo scratch) degrades to its plain scissor — counted on <c>stencilFallback</c>, never silently wrong.</summary>
    private static bool StencilTargetSupported => true;

    // The DSV's minimum size while compositing: tiles and region scratches can exceed a small window.
    private int _stencilFloorW, _stencilFloorH;

    /// <summary>Create the stencil surface + its 1-slot DSV heap on the FIRST stencil scope of the process (an app that
    /// never clips to a path allocates nothing). Sized to the swapchain and never grown in place: <see cref="Resize"/>
    /// releases it behind its own WaitForGpu, so the next scope recreates it at the new size.</summary>
    private bool EnsureStencilDsv(int w, int h)
    {
        if (_device == null || w <= 0 || h <= 0) return false;
        if (_f!.StencilDsv != null)
        {
            if (StencilDsvPolicy.Covers(_f!.StencilW, _f!.StencilH, w, h)) return true;
            // EXACT match required (CANDIDATE FIX, INCIDENT 2026-09) — this used to be `_f!.StencilW >= w && _f!.StencilH
            // >= h`, which let an OVERSIZED DSV silently satisfy a smaller target once this field was still
            // device-global (a 480×270 detached-child video pop-out target could be bound with the main window's
            // full-size DSV). Now that _f!.StencilDsv/_f!.DsvHeap/_f!.StencilW/_f!.StencilH are per-target (Activate/StoreActive
            // load/store D3D12Swapchain.StencilDsv* above), a mismatch here means THIS target's own DSV is stale —
            // it was created at an earlier size and the target resized. DO NOT release+recreate it HERE: this method
            // is called mid-recording, from RebindCurrentTarget deep inside SubmitDrawList, AFTER FlushUploads has
            // already snapshotted `_retireFence = _fenceValue + 1` to stamp this frame's Image-store retires
            // (D3D12Device.cs ~1846 / ImageTextureStore.FlushUploads). A WaitForGpu() here would bump `_fenceValue`
            // PAST that snapshot — the exact "retirements stamped with _fenceValue + 1 before it advances again"
            // invariant PrepareGlyphs' own atlas-growth comment (":1726") already documents and relies on. Recreating
            // stale-sized DSVs is instead handled EAGERLY at the top of SubmitDrawList, right after Activate(sc) and
            // BEFORE PrepareGlyphs/FlushUploads run — see the resize check there. Reaching a mismatch HERE means that
            // eager check raced a same-frame resize it couldn't see yet (or a caller outside SubmitDrawList's normal
            // Activate-then-record order); degrade to the SAME plain-scissor fallback StencilTargetSupported already
            // uses for a region-local surface — never wrong, just conservative — and let the NEXT frame's eager check
            // catch up. Counted on _frameStencilFallback exactly like that path (see the caller).
            return false;
        }
        int cw = Math.Max(Math.Max(w, (int)_w), _stencilFloorW), ch = Math.Max(Math.Max(h, (int)_h), _stencilFloorH);

        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = (ulong)cw;
        rd.Height = (uint)ch;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = StencilPso.DsvFormat;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

        D3D12_CLEAR_VALUE cv = default;
        cv.Format = StencilPso.DsvFormat;
        cv.Anonymous.DepthStencil.Depth = 1f;
        cv.Anonymous.DepthStencil.Stencil = 0;

        // Query BEFORE CreateCommittedResource, on the SAME desc (audit gpu mem-02): the device-reported allocation
        // requirement, not an inferred cw*ch*4 pixel estimate — see D3D12MemoryDiagnostics.AllocationBytes.
        ulong dsvBytes = D3D12MemoryDiagnostics.AllocationBytes(_device, &rd);

        ID3D12Resource* res;
        // PERMANENT DEPTH_WRITE: nothing ever reads this as an SRV or copies it, so it needs no barrier for its whole
        // life — which is what keeps a stencil scope free of the barrier cost a transient attachment would carry.
        if ((int)_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv,
                __uuidof<ID3D12Resource>(), (void**)&res) < 0)
            return false;

        if (_f!.DsvHeap == null)
        {
            D3D12_DESCRIPTOR_HEAP_DESC hd = default;
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
            hd.NumDescriptors = 1;
            ID3D12DescriptorHeap* heap;
            if ((int)_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap) < 0)
            {
                res->Release();
                return false;
            }
            _f!.DsvHeap = heap;
        }
        _device->CreateDepthStencilView(res, null, _f!.DsvHeap->GetCPUDescriptorHandleForHeapStart());
        D3D12MemoryDiagnostics.Track(res, dsvBytes != 0 ? "StencilClip.Dsv" : "StencilClip.AllocationUnknown.Dsv",
            dsvBytes != 0 ? dsvBytes : (uint)(cw * ch * 4));
        _f!.StencilDsv = res;
        _f!.StencilW = cw; _f!.StencilH = ch;
        Rec(RecordedOp.StencilDsvCreated, (uint)cw, (uint)ch);
        return true;
    }

    // Releases THIS target's DSV + heap and resets its sizes (INCIDENT 2026-09 §1.4/§2.3.3; Phase 1 §3.1/§3.3
    // structurally removes the class of bug this guarded against). There is no more device-global "working copy" to
    // keep in sync — f.StencilDsv/DsvHeap/StencilW/StencilH ARE the target's storage, so releasing them here is the
    // whole story; no `ReferenceEquals(target, _activeSwapchain)` branch is needed any more. Never call mid-scope (see
    // EnsureStencilDsv's comment); callers: SubmitDrawList's eager stale-size check, Resize, ReleaseSwapchainResources.
    private void ReleaseStencilDsv(TargetFrameState f)
    {
        AssertDeviceOwner();
        if (f.StencilDsv != null)
        {
            D3D12MemoryDiagnostics.Release(f.StencilDsv, "StencilClip.Dsv");
            f.StencilDsv->Release();
            f.StencilDsv = null;
        }
        if (f.DsvHeap != null) { f.DsvHeap->Release(); f.DsvHeap = null; }
        f.StencilW = 0; f.StencilH = 0;
        f.StencilDepth = 0; f.StencilScopeMasked.Clear(); f.StencilDsvBound = false;
    }

    /// <summary>Re-attach (or drop) the stencil DSV on the CURRENT scene render target. OMSetRenderTargets disturbs
    /// neither viewport, scissor, PSO nor root bindings, so this is safe to issue mid-segment.</summary>
    private void RebindCurrentTarget(bool withDsv, D3D12_CPU_DESCRIPTOR_HANDLE rtv)
    {
        // Inside a render pass the target is bound by the pass (no DSV): dropping the DSV is already the state, and
        // attaching one leaves the pass for the legacy binding for the rest of this target's draws.
        if (_inRenderPass)
        {
            if (!withDsv) { _f!.StencilDsvBound = false; return; }
            EndPassIfOpen();
        }
        if (withDsv && StencilTargetSupported && EnsureStencilDsv(TargetW, TargetH))
        {
            var dsv = _f!.DsvHeap->GetCPUDescriptorHandleForHeapStart();
            _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, &dsv);
            // The §1.2 suspect: an RTV bound with a DSV of different dimensions fails Close with E_INVALIDARG. B names
            // the DSV size; the target size is the preceding Viewport/ListReset entry.
            Rec(RecordedOp.SetRenderTargetWithDsv, (uint)rtv.ptr, (uint)_f!.StencilW << 16 | ((uint)_f!.StencilH & 0xFFFF));
            _f!.StencilDsvBound = true;
            return;
        }
        _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        Rec(RecordedOp.SetRenderTarget, (uint)rtv.ptr);
        _f!.StencilDsvBound = false;
    }

    // The mask draw reuses the FillPath lane verbatim: the SAME PathRealizationCache realization, the same instance
    // record shape. Color is irrelevant (the mask PSO masks colour writes off entirely) and trim/dash carry the
    // full-cover window, exactly like a fill.
    private static PathDrawItem StencilMaskItem(int vtxStart, int vtxCount, int idxStart, int idxCount, in Affine2D t)
        => new()
        {
            VtxStart = vtxStart, VtxCount = vtxCount, IdxStart = idxStart, IdxCount = idxCount,
            Inst = new PathInstance
            {
                R = 0f, G = 0f, B = 0f, A = 1f,
                M11 = t.M11, M12 = t.M12, M21 = t.M21, M22 = t.M22, Dx = t.Dx, Dy = t.Dy,
                Opacity = 1f, ArcLenPx = 0f, TrimStart = 0f, TrimEnd = 1f, DashOn = 0f, DashOff = 0f,
            },
        };

    // Clear the stencil over exactly the pixels this scope can paint. _lastScissor is already target-relative and
    // clamped by SetScissorRect (the same arithmetic the scissor itself uses), so the clear and the scope agree by
    // construction rather than by two copies of the conversion.
    private void ClearStencilRect()
    {
        RECT r = _lastScissor;
        if (r.right <= r.left || r.bottom <= r.top) return;
        // A Clear* is illegal inside a render pass: the stencil scope leaves the pass here (it binds the target
        // legacy-style with its DSV right after, for the rest of this target's draws).
        EndPassIfOpen();
        _cmdList->ClearDepthStencilView(_f!.DsvHeap->GetCPUDescriptorHandleForHeapStart(),
            D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_STENCIL, 1f, 0, 1, &r);
        Rec(RecordedOp.ClearDsv, (uint)r.left << 16 | ((uint)r.top & 0xFFFF), (uint)r.right << 16 | ((uint)r.bottom & 0xFFFF));
    }

    private void BeginStencilScope(in PushStencilClipCmd c, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE rtv)
    {
        _frameStencilClips++;
        FlushSegment(lw, lh);                                   // (1) the non-reorderable pass boundary
        // (2) a stencil clip IS ALSO a tier-1 scissor push. Routing DeviceRect through PushScissor is what keeps
        //     _clipStack and _roundedClipStack index-parallel — and a plain ClipCmd inherits the enclosing ROUNDED
        //     entry, so a heart nested inside a rounded card keeps the card's corner clamp too.
        PushScissor(new ClipCmd(c.DeviceRect));
        EnsureDesiredScissor(CurrentScissorRect(), lw, lh);
        SetScissorRect(_desiredScissor);                        // nothing is pending after the flush ⇒ applies now

        bool masked = c.VtxCount > 0 && c.IdxCount > 0
                      && StencilTargetSupported
                      && (_pathPipe?.EnsureStencilReady() ?? false)
                      && EnsureStencilDsv(TargetW, TargetH);
        if (!masked)
        {
            // Honest degradation, never a dropped clip: the scope's scissor still bounds it to the geometry's AABB,
            // and whatever mask already applies keeps applying. Counted so a live session can SEE it happen.
            _frameStencilFallback++;
            _f!.StencilScopeMasked.Add(false);
            return;
        }

        if (_f!.StencilDepth == 0) ClearStencilRect();             // (3) outermost push owns the clear of its own rect
        RebindCurrentTarget(withDsv: true, rtv);                // (4) viewport/scissor untouched
        Affine2D maskXf = c.Transform;   // a positional record's member is a PROPERTY — bind it so `in` is legal
        var item = StencilMaskItem(c.VtxStart, c.VtxCount, c.IdxStart, c.IdxCount, in maskXf);
        _pathPipe!.RecordStencilMask(_cmdList, in item, lw, lh, decr: false);   // (5)
        InvalidateCmdState();                                   // the mask bound its own PSO + IA state
        _f!.StencilDepth++;                                        // (6)
        _f!.StencilScopeMasked.Add(true);
        _cmdList->OMSetStencilRef((uint)_f!.StencilDepth);
        Rec(RecordedOp.StencilRef, (uint)_f!.StencilDepth);
    }

    private void EndStencilScope(in PopStencilClipCmd c, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE rtv)
    {
        FlushSegment(lw, lh);                                   // (1) land the scope's draws while its mask is live
        bool masked = _f!.StencilScopeMasked.Count > 0 && _f!.StencilScopeMasked[^1];
        if (_f!.StencilScopeMasked.Count > 0) _f!.StencilScopeMasked.RemoveAt(_f!.StencilScopeMasked.Count - 1);
        if (masked)
        {
            _f!.StencilDepth--;                                    // (2)
            if (_f!.StencilDepth > 0)
            {
                // (3) erase THIS level by re-drawing the push's geometry with DECR_SAT. A nested PushLayer may have
                //     detached the DSV on its way back out; re-attach before the erase or the outer scope inherits a
                //     mask level that was never removed.
                if (!_f!.StencilDsvBound) RebindCurrentTarget(withDsv: true, rtv);
                if (c.VtxCount > 0 && c.IdxCount > 0 && _f!.StencilDsvBound)
                {
                    Affine2D maskXf = c.Transform;   // ditto — the pop re-carries the push's transform byte-identically
                    var item = StencilMaskItem(c.VtxStart, c.VtxCount, c.IdxStart, c.IdxCount, in maskXf);
                    _pathPipe!.RecordStencilMask(_cmdList, in item, lw, lh, decr: true);
                    InvalidateCmdState();
                }
                _cmdList->OMSetStencilRef((uint)_f!.StencilDepth);
                Rec(RecordedOp.StencilRef, (uint)_f!.StencilDepth);
            }
            else
            {
                // Back to depth 0: skip the erase entirely — the NEXT outermost push clears its own rect — and drop the
                // DSV so the rest of the frame runs on the untouched, DSV-free path.
                RebindCurrentTarget(withDsv: false, rtv);
                InvalidateCmdState();
            }
        }
        PopScissor();                                           // (4)
        EnsureDesiredScissor(CurrentScissorRect(), lw, lh);
    }

    // End-of-submit safety net for an UNBALANCED stream (mirrors _clipStack.Clear()): clamp the depth back to 0 and
    // detach the DSV so nothing after the scene inherits a stencil test or a bound attachment.
    private void EndStencilScopesAtSubmitEnd(D3D12_CPU_DESCRIPTOR_HANDLE rtv)
    {
        _f!.StencilScopeMasked.Clear();
        if (_f!.StencilDepth != 0 || _f!.StencilDsvBound)
        {
            _f!.StencilDepth = 0;
            _cmdList->OMSetStencilRef(0);
            Rec(RecordedOp.StencilRef, 0);
            RebindCurrentTarget(withDsv: false, rtv);
            InvalidateCmdState();
        }
    }

    /// <summary>Stamp the innermost rounded clip (if any) onto a RoundRect-pipeline instance (the PS multiplies its
    /// coverage by the rounded-box SDF — the tier-2 path for animated clips on rounded surfaces). Skip the stamp when
    /// the instance's axis-aligned device AABB lies inside the clip deflated by <c>radius + aaSlack</c> — the SDF would
    /// evaluate to full coverage anyway (pixel-identical), and leaving <c>ClipW ≤ 0</c> unlocks the opaque no-blend PSO
    /// for interior fills once the content plate is opaque.</summary>
    private void ApplyRoundedClip(ref RectInstance inst)
    {
        if (_roundedClipStack.Count == 0) return;
        var (rect, radius) = _roundedClipStack[^1];
        if (rect.W <= 0f) return;
        if (RoundedClipRedundant(rect, radius, inst.PosX, inst.PosY, inst.W, inst.H, inst.M11, inst.M12, inst.M21, inst.M22, inst.Dx, inst.Dy))
            return;
        inst.ClipX = rect.X; inst.ClipY = rect.Y; inst.ClipW = rect.W; inst.ClipH = rect.H; inst.ClipR = radius;
    }

    private void ApplyRoundedClip(ref ImageInstance inst)
    {
        if (_roundedClipStack.Count == 0) return;
        var (rect, radius) = _roundedClipStack[^1];
        if (rect.W <= 0f) return;
        if (RoundedClipRedundant(rect, radius, inst.PosX, inst.PosY, inst.W, inst.H, inst.M11, inst.M12, inst.M21, inst.M22, inst.Dx, inst.Dy))
            return;
        inst.ClipX = rect.X; inst.ClipY = rect.Y; inst.ClipW = rect.W; inst.ClipH = rect.H; inst.ClipR = radius;
    }

    private void ApplyRoundedClip(ref GradientInstance inst)
    {
        if (_roundedClipStack.Count == 0) return;
        var (rect, radius) = _roundedClipStack[^1];
        if (rect.W <= 0f) return;
        if (RoundedClipRedundant(rect, radius, inst.PosX, inst.PosY, inst.W, inst.H, inst.M11, inst.M12, inst.M21, inst.M22, inst.Dx, inst.Dy))
            return;
        inst.ClipX = rect.X; inst.ClipY = rect.Y; inst.ClipW = rect.W; inst.ClipH = rect.H; inst.ClipR = radius;
    }

    /// <summary>~1 DIP AA feather (matches the RoundRect PS soft edge) — deflate by radius+slack before claiming the
    /// instance is fully inside. Stamp on any doubt (rotated/skewed transform, degenerate rect).</summary>
    private const float RoundedClipAaSlack = 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RoundedClipRedundant(in RectF clip, float radius,
        float posX, float posY, float w, float h,
        float m11, float m12, float m21, float m22, float dx, float dy)
    {
        if (w <= 0f || h <= 0f) return false;
        // Axis-aligned only — a rotation/skew means the local AABB is not the device AABB.
        if (m12 != 0f || m21 != 0f || m11 <= 0f || m22 <= 0f) return false;
        float inset = radius + RoundedClipAaSlack;
        if (clip.W <= 2f * inset || clip.H <= 2f * inset) return false;
        // Local rect → device via scale+translate (linear-identity aside from uniform/non-uniform scale).
        float x0 = posX * m11 + dx, y0 = posY * m22 + dy;
        float x1 = (posX + w) * m11 + dx, y1 = (posY + h) * m22 + dy;
        float l = MathF.Min(x0, x1), r = MathF.Max(x0, x1);
        float t = MathF.Min(y0, y1), b = MathF.Max(y0, y1);
        return l >= clip.X + inset && t >= clip.Y + inset
            && r <= clip.X + clip.W - inset && b <= clip.Y + clip.H - inset;
    }

    private void ApplyCurrentScissor()
    {
        ResetDesiredScissor();
    }

    // Bookkeep a pipeline Record outcome: `recorded` = the run actually recorded (its state is now bound);
    // `rb` = it was asked to fully rebind (a false rb that recorded reused the cross-segment bound state).
    private void NotePipeBind(bool recorded, bool rb, BoundPipe pipe)
    {
        if (!recorded) return;
        _boundPipe = pipe;
        _sharedSdfStateBound = false;
        if (rb) _framePipeBinds++; else _framePipeBindsSkipped++;
    }

    private void NoteSdfPipeBind(bool recorded, bool bindSharedState, bool bindPipelineState, BoundPipe pipe)
    {
        if (!recorded) return;
        _boundPipe = pipe;
        _sharedSdfStateBound = true;
        if (bindSharedState || bindPipelineState) _framePipeBinds++; else _framePipeBindsSkipped++;
    }

    // Classifier for the RoundRect opaque fast path: a fill that writes fully-opaque, pixel-crisp pixels — square (no
    // rounded corners ⇒ no SDF AA feather), unstroked, plain kind (not checker/tab), fully opaque, and outside any tier-2
    // rounded clip (a rounded clip needs the coverage-multiply, hence blend). Conservative: any doubt ⇒ the blended path.
    private static bool IsOpaquePlainRect(in RectInstance r) =>
        r.Kind == 0f && r.StrokeWidth == 0f
        && r.RTL == 0f && r.RTR == 0f && r.RBR == 0f && r.RBL == 0f
        && r.Opacity >= 1f && r.A >= 1f
        && r.ClipW <= 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteRectSubmitted(ReadOnlySpan<RectInstance> instances, bool opaque)
    {
#if DEBUG || FLUENTGPU_DIAG
        if (!s_rectAreaDiag) return;
        for (int i = 0; i < instances.Length; i++) NoteRectSubmitted(in instances[i], opaque);
#endif
    }

    private void NoteRectSubmitted(in RectInstance r, bool opaque)
    {
        int ordinal = _frameRectAreaOrdinal++;
        double determinant = (double)r.M11 * r.M22 - (double)r.M12 * r.M21;
        double scale = _frameScale;
        double areaPx2 = Math.Abs((double)r.W * r.H * determinant) * scale * scale;
        if (!double.IsFinite(areaPx2) || areaPx2 <= 0.0) return;
        if (opaque)
        {
            _frameRectOpaqueSubmittedPx2 += areaPx2;
            return;
        }

        _frameRectBlendedSubmittedPx2 += areaPx2;
        float alpha = r.A * r.Opacity;
        if (!float.IsFinite(alpha)) alpha = 0f;
        alpha = Math.Clamp(alpha, 0f, 1f);
        RectSubmittedAreaFlags flags = RectSubmittedAreaFlags.None;
        if (r.RTL != 0f || r.RTR != 0f || r.RBR != 0f || r.RBL != 0f) flags |= RectSubmittedAreaFlags.Rounded;
        if (r.StrokeWidth != 0f) flags |= RectSubmittedAreaFlags.Stroked;
        if (r.ClipW > 0f) flags |= RectSubmittedAreaFlags.RoundedClip;
        if (r.Kind != 0f) flags |= RectSubmittedAreaFlags.NonPlainKind;
        var sample = new RectSubmittedAreaItem(ordinal, areaPx2, alpha, r.W, r.H, flags);

        int insert = 0;
        while (insert < _frameBlendedTopCount && _frameBlendedTop[insert].AreaPx2 >= areaPx2) insert++;
        if (insert >= BlendedRectAreaTopCount) return;
        int nextCount = Math.Min(_frameBlendedTopCount + 1, BlendedRectAreaTopCount);
        for (int j = nextCount - 1; j > insert; j--) _frameBlendedTop[j] = _frameBlendedTop[j - 1];
        _frameBlendedTop[insert] = sample;
        _frameBlendedTopCount = nextCount;
    }

    private int DroppedInstanceCount()
        => (_rectPipe?.DroppedInstances ?? 0) + (_shadowPipe?.DroppedInstances ?? 0) +
           (_arcPipe?.DroppedInstances ?? 0) + (_polylinePipe?.DroppedInstances ?? 0) +
           (_gradPipe?.DroppedInstances ?? 0) + (_glyphs?.DroppedInstances ?? 0) +
           (_imagePipe?.DroppedInstances ?? 0) + (_pathPipe?.DroppedInstances ?? 0) +
           (_seriesPipe?.DroppedInstances ?? 0) + (_spritePipe?.DroppedInstances ?? 0);

    // <paramref name="stencilTest"/> ⇒ every COVERED pipeline binds its EQUAL-tested clone (the device has already
    // programmed OMSetStencilRef with the live scope depth). The uncovered arms — Shadow, Arc, Polyline, Series and the DestOut
    // video-hole punch — record exactly as they always did and are counted on `stencilFallback`: inside a scope they are
    // clipped by the scope's SCISSOR only. That is the honest v1 coverage scope (D6), the same posture the tier-2
    // rounded clip already documents on ClipCmd.
    private void RecordAll(float lw, float lh, bool stencilTest)
    {
        // Replay non-glyph primitives in painter (stream) order so a shadow sits OVER the background drawn before it and
        // UNDER its own element. Consecutive same-kind ops are still one batched draw. A run whose pipeline is ALREADY
        // bound on the command list (tracked in _boundPipe across segment flushes) skips the static rebind and records
        // only its SRV offset + draw — see the state-cache comment on _boundPipe. Glyphs render last WITHIN A SEGMENT —
        // text on top of the fills recorded before it; a fill recorded OVER pending text has already cut the segment
        // (CoverPendingText), so stream order survives the batching.
        if (_runs.Count > 0)
        {
            var rectSpan = CollectionsMarshal.AsSpan(_rectInsts);
            var shadowSpan = CollectionsMarshal.AsSpan(_shadowInsts);
            var arcSpan = CollectionsMarshal.AsSpan(_arcInsts);
            var polylineSpan = CollectionsMarshal.AsSpan(_polylineInsts);
            var gradSpan = CollectionsMarshal.AsSpan(_gradInsts);
            var pathSpan = CollectionsMarshal.AsSpan(_pathDraws);
            var seriesSpan = CollectionsMarshal.AsSpan(_seriesInsts);
            var spriteSpan = CollectionsMarshal.AsSpan(_spriteInsts);
            int rc = 0, sc = 0, ac = 0, pc = 0, gc = 0, ic = 0, pdc = 0, sec = 0, spc = 0;
            _frameRuns += _runs.Count;
            foreach (var (kind, count) in _runs)
            {
                switch (kind)
                {
                    case PrimKind.Shadow:
                        bool bindShadowShared = !_sharedSdfStateBound;
                        bool bindShadowPso = _boundPipe != BoundPipe.Shadow;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_shadowPipe!.Record(_cmdList, shadowSpan.Slice(sc, count), lw, lh, bindShadowShared, bindShadowPso),
                            bindShadowShared, bindShadowPso, BoundPipe.Shadow);
                        sc += count; break;
                    case PrimKind.Arc:
                        bool bindArcShared = !_sharedSdfStateBound;
                        bool bindArcPso = _boundPipe != BoundPipe.Arc;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_arcPipe!.Record(_cmdList, arcSpan.Slice(ac, count), lw, lh, bindArcShared, bindArcPso),
                            bindArcShared, bindArcPso, BoundPipe.Arc);
                        ac += count; break;
                    case PrimKind.Polyline:
                        bool bindPolylineShared = !_sharedSdfStateBound;
                        bool bindPolylinePso = _boundPipe != BoundPipe.Polyline;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_polylinePipe!.Record(_cmdList, polylineSpan.Slice(pc, count), lw, lh, bindPolylineShared, bindPolylinePso),
                            bindPolylineShared, bindPolylinePso, BoundPipe.Polyline);
                        pc += count; break;
                    case PrimKind.Series:
                        bool bindSeriesShared = !_sharedSdfStateBound;
                        bool bindSeriesPso = _boundPipe != BoundPipe.Series;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_seriesPipe!.Record(_cmdList, seriesSpan.Slice(sec, count), lw, lh, bindSeriesShared, bindSeriesPso),
                            bindSeriesShared, bindSeriesPso, BoundPipe.Series);
                        sec += count; break;
                    case PrimKind.Sprites:
                    case PrimKind.SpritesAdd:
                    {
                        bool add = kind == PrimKind.SpritesAdd;
                        var want = add ? BoundPipe.SpritesAdd : BoundPipe.Sprites;
                        bool bindSpShared = !_sharedSdfStateBound;
                        bool bindSpPso = _boundPipe != want;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_spritePipe!.Record(_cmdList, spriteSpan.Slice(spc, count), lw, lh, bindSpShared, bindSpPso, additive: add),
                            bindSpShared, bindSpPso, want);
                        spc += count; break;
                    }
                    case PrimKind.RectAdd:
                    {
                        // Additive rects: the rect instance list, the additive PSO; no opaque fast path, no stencil clone
                        // (inside a stencil scope they are scissor-clipped only, like the other uncovered pipelines).
                        var addRun = rectSpan.Slice(rc, count);
                        rc += count;
                        bool bindAddShared = !_sharedSdfStateBound;
                        bool bindAddPso = _boundPipe != BoundPipe.RectAdd;
                        if (stencilTest) _frameStencilFallback += count;
                        NoteSdfPipeBind(_rectPipe!.Record(_cmdList, addRun, lw, lh, bindAddShared, bindAddPso, RectPass.Additive),
                            bindAddShared, bindAddPso, BoundPipe.RectAdd);
                        break;
                    }
                    case PrimKind.GradientAdd:
                    {
                        bool bindGaShared = !_sharedSdfStateBound;
                        bool bindGaPso = _boundPipe != BoundPipe.GradientAdd;
                        if (stencilTest) _frameStencilFallback += count;
                        NoteSdfPipeBind(_gradPipe!.Record(_cmdList, gradSpan.Slice(gc, count), lw, lh, bindGaShared, bindGaPso, stencilTest: false, additive: true),
                            bindGaShared, bindGaPso, BoundPipe.GradientAdd);
                        gc += count; break;
                    }
                    case PrimKind.SeriesAdd:
                    {
                        bool bindSaShared = !_sharedSdfStateBound;
                        bool bindSaPso = _boundPipe != BoundPipe.SeriesAdd;
                        if (stencilTest) _frameStencilFallback += count;
                        NoteSdfPipeBind(_seriesPipe!.Record(_cmdList, seriesSpan.Slice(sec, count), lw, lh, bindSaShared, bindSaPso, additive: true),
                            bindSaShared, bindSaPso, BoundPipe.SeriesAdd);
                        sec += count; break;
                    }
                    case PrimKind.Gradient:
                        bool bindGradientShared = !_sharedSdfStateBound;
                        bool bindGradientPso = _boundPipe != BoundPipe.Gradient;
                        NoteSdfPipeBind(_gradPipe!.Record(_cmdList, gradSpan.Slice(gc, count), lw, lh, bindGradientShared, bindGradientPso, stencilTest),
                            bindGradientShared, bindGradientPso, BoundPipe.Gradient);
                        gc += count; break;
                    case PrimKind.Rect:
                        var rectRun = rectSpan.Slice(rc, count);
                        rc += count;
                        // Opaque fast path: split the run into maximal same-class (opaque plain vs everything-else) sub-runs
                        // IN PAINTER ORDER (never reordered) and draw each with its PSO — opaque fills skip alpha blend +
                        // the SDF shader (the dominant background/panel pixels). Disabled ⇒ one blended run, exactly as before.
                        // Inside a stencil scope the opaque fast path is DISABLED: there is no stencil-tested clone of the
                        // no-blend PSO (D6), and an untested opaque plate would paint straight over the clip silhouette.
                        // One blended, stencil-tested run instead — pixel-identical output, just without the fast path.
                        if (!_rectPipe!.HasOpaquePso || stencilTest)
                        {
                            bool bindRectShared = !_sharedSdfStateBound;
                            bool bindRectPso = _boundPipe != BoundPipe.Rect;
                            _frameRectBlendedInsts += rectRun.Length;   // opaque PSO unavailable ⇒ every instance blends
                            NoteRectSubmitted(rectRun, opaque: false);
                            NoteSdfPipeBind(_rectPipe!.Record(_cmdList, rectRun, lw, lh, bindRectShared, bindRectPso, RectPass.Blended, stencilTest),
                                bindRectShared, bindRectPso, BoundPipe.Rect);
                            break;
                        }
                        for (int si = 0; si < rectRun.Length;)
                        {
                            bool op = IsOpaquePlainRect(in rectRun[si]);
                            int sj = si + 1;
                            while (sj < rectRun.Length && IsOpaquePlainRect(in rectRun[sj]) == op) sj++;
                            if (op) _frameRectOpaqueInsts += sj - si; else _frameRectBlendedInsts += sj - si;
                            NoteRectSubmitted(rectRun.Slice(si, sj - si), op);
                            var want = op ? BoundPipe.RectOpaque : BoundPipe.Rect;
                            bool bindShared = !_sharedSdfStateBound;
                            bool bindPso = _boundPipe != want;
                            NoteSdfPipeBind(_rectPipe!.Record(_cmdList, rectRun.Slice(si, sj - si), lw, lh, bindShared, bindPso, op ? RectPass.Opaque : RectPass.Blended),
                                bindShared, bindPso, want);
                            si = sj;
                        }
                        break;
                    case PrimKind.VideoHole:
                        // The hole punch rides the rect instance buffer (so `rc` MUST advance with it) but always draws
                        // through the DestOut PSO — never the opaque/blended segmentation above.
                        var holeRun = rectSpan.Slice(rc, count);
                        rc += count;
                        bool bindHoleShared = !_sharedSdfStateBound;
                        bool bindHolePso = _boundPipe != BoundPipe.RectDestOut;
                        if (stencilTest) _frameStencilFallback += count;   // DestOut has no tested clone: scissor only
                        NoteSdfPipeBind(_rectPipe!.Record(_cmdList, holeRun, lw, lh, bindHoleShared, bindHolePso, RectPass.DestOut),
                            bindHoleShared, bindHolePso, BoundPipe.RectDestOut);
                        break;
                    case PrimKind.Image:
                        // stencilTest forces the rebind: _boundPipe tracks WHICH pipe is bound, not which VARIANT.
                        if (_boundPipe != BoundPipe.Image || stencilTest)
                        {
                            _imagePipe!.Begin(_cmdList, _imageTextures!.Heap, lw, lh, stencilTest);   // (re)bind heap/PSO/root-sig/VB for this image run
                            _boundPipe = BoundPipe.Image;
                            _sharedSdfStateBound = false;
                            _framePipeBinds++;
                        }
                        else _framePipeBindsSkipped++;
                        int end = ic + count;
                        for (int k = ic; k < end;)
                        {
                            var (inst, id) = _imageDraws[k];
                            if (!_imageTextures!.TryGet(id, out var srv, out var uv))
                            {
                                _frameImageSkipped++;   // image recorded but its texture isn't live yet (diagnostic: should be 0 once loaded)
                                k++;
                                continue;
                            }

                            _imageRangeScratch.Clear();
                            do
                            {
                                var d = inst;
                                // Compose the atlas cell (uv) with the content-fit sub-rect baked on the instance (inst.Uv*,
                                // 0..1 source space): origin = cell.origin + fit.origin*cell.size, size = cell.size*fit.size.
                                // Whole-texture images (cell = 0,0,1,1) pass the content-fit rect through unchanged.
                                d.UvX = uv.X + inst.UvX * uv.W; d.UvY = uv.Y + inst.UvY * uv.H;
                                d.UvW = uv.W * inst.UvW; d.UvH = uv.H * inst.UvH;
                                _imageRangeScratch.Add(d);
                                k++;
                                if (k >= end) break;
                                (inst, id) = _imageDraws[k];
                            }
                            while (_imageTextures.TryGet(id, out var nextSrv, out uv) && nextSrv.ptr == srv.ptr);

                            _imagePipe!.DrawRange(_cmdList, srv, CollectionsMarshal.AsSpan(_imageRangeScratch));
                        }
                        ic = end;
                        break;
                    case PrimKind.Path:
                        // TRAP (see PathPipeline's type doc): Path binds its OWN vertex buffer, index buffer, and
                        // TRIANGLELIST topology — it does NOT participate in the five-pipe shared-SDF-state dedup
                        // (_sharedSdfStateBound), so this MUST follow the Image pattern (explicit Begin + clear the
                        // flag) and MUST NOT route through NoteSdfPipeBind. Leaving _sharedSdfStateBound true here
                        // would make the NEXT Rect/Arc/Polyline/Gradient run skip rebinding the shared quad VB +
                        // TRIANGLESTRIP topology and draw with Path's VB/IB/topology still bound — silent,
                        // intermittent corruption that no headless gate can catch (needs real GPU pixels to see).
                        if (_boundPipe != BoundPipe.Path || stencilTest)
                        {
                            _pathPipe!.Begin(_cmdList, lw, lh, stencilTest);
                            _boundPipe = BoundPipe.Path;
                            _sharedSdfStateBound = false;
                            _framePipeBinds++;
                        }
                        else _framePipeBindsSkipped++;
                        int pend = pdc + count;
                        for (; pdc < pend; pdc++) _pathPipe!.Record(_cmdList, in pathSpan[pdc]);
                        break;
                }
            }
        }
        if (_glyphInsts.Count > 0)
        {
            bool rb = _boundPipe != BoundPipe.Glyph;
            NotePipeBind(_glyphs!.Record(_cmdList, _glyphInsts, lw, lh, rb, stencilTest), rb, BoundPipe.Glyph);
        }
        if (_gradGlyphInsts.Count > 0)   // sub-glyph wipe, same RT/z as glyphs
        {
            bool rb = _boundPipe != BoundPipe.GradGlyph;
            NotePipeBind(_glyphs!.RecordGradient(_cmdList, _gradGlyphInsts, lw, lh, rb, stencilTest), rb, BoundPipe.GradGlyph);
        }
    }

    private void FlushSegment(float lw, float lh)
    {
        if (!HasPendingSegment()) return;
        _frameSegments++;
        if (_desiredScissorValid) SetScissorRect(_desiredScissor);
        else ResetDesiredScissor();
        if (_glyphs!.NeedsUpload)
        {
            // A glyph packed after the frame-start band (never expected — every decoded stream was prepared first):
            // copies are illegal inside a render pass, so step out for it.
            bool reopen = _inRenderPass;
            EndPassIfOpen();
            _glyphs.UploadIfDirty(_cmdList);
            if (reopen) ResumePass();
        }
        // A tier-3 stencil scope only tests when a DSV is actually attached — a nested PushLayer subtree renders into a
        // target that has none, and is documented + counted as scissor-only there (D6).
        bool stencilTest = _f!.StencilDepth > 0 && _f!.StencilDsvBound;
        if (stencilTest) _cmdList->OMSetStencilRef((uint)_f!.StencilDepth);   // idempotent safety across compositor passes
        RecordAll(lw, lh, stencilTest);
        ClearInsts();
    }

    // <paramref name="targetRtv"/> is the RTV this walk is painting into (the back buffer on the FullDirect route, a
    // tile or degraded chunk on the composite route). A tier-3 stencil scope needs it in order to re-attach the target WITH the
    // depth-stencil view — the only reason a streaming walk needs to know its own target at all.
    private void SubmitStreaming(ReadOnlySpan<byte> drawList, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE targetRtv)
    {
        _streamLw = lw; _streamLh = lh;
        ClearInsts();
        _blendAdditive = false;   // a walk never inherits the previous stream's paint blend
        _clipStack.Clear();
        _roundedClipStack.Clear();
        ResetDesiredScissor();
        int pos = 0;
        while (pos + sizeof(int) <= drawList.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(drawList.Slice(pos));
            if (op == DrawOp.PushClip)
            {
                pos += sizeof(int);
                var clip = MemoryMarshal.Read<ClipCmd>(drawList.Slice(pos));
                pos += Unsafe.SizeOf<ClipCmd>();
                _frameClipOps++;
                PushScissor(in clip);
                EnsureDesiredScissor(CurrentScissorRect(), lw, lh);
                continue;
            }
            if (op == DrawOp.PopClip)
            {
                pos += sizeof(int);
                _frameClipOps++;
                PopScissor();
                EnsureDesiredScissor(CurrentScissorRect(), lw, lh);
                continue;
            }
            if (op == DrawOp.PushStencilClip)
            {
                pos += sizeof(int);
                var sc = MemoryMarshal.Read<PushStencilClipCmd>(drawList.Slice(pos));
                pos += Unsafe.SizeOf<PushStencilClipCmd>();
                _frameClipOps++;
                BeginStencilScope(in sc, lw, lh, targetRtv);
                continue;
            }
            if (op == DrawOp.PopStencilClip)
            {
                pos += sizeof(int);
                var sc = MemoryMarshal.Read<PopStencilClipCmd>(drawList.Slice(pos));
                pos += Unsafe.SizeOf<PopStencilClipCmd>();
                _frameClipOps++;
                EndStencilScope(in sc, lw, lh, targetRtv);
                continue;
            }
            pos = DecodeOne(drawList, pos);
        }
        FlushSegment(lw, lh);
        EndStencilScopesAtSubmitEnd(targetRtv);
        _clipStack.Clear();
        _roundedClipStack.Clear();
        _desiredScissor = FullScissorRect();
        _desiredScissorValid = true;
        SetFullScissor();
    }

    private void Barrier(ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Flags = D3D12_RESOURCE_BARRIER_FLAGS.D3D12_RESOURCE_BARRIER_FLAG_NONE;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        _cmdList->ResourceBarrier(1, &b);
        Rec(RecordedOp.Barrier, (uint)(nint)res, (uint)before << 16 | ((uint)after & 0xFFFF));   // a=0 ⇒ a null pResource (§1.2 class 2)
        if (res == _frameBackBuffer) _frameBackBufferTransitions++;   // GpuFrameCounters / GpuPassFrameSummary.BackBufferTransitions
    }

    internal bool Vsync { get => _vsync; set => _vsync = value; }

    // noWait: Present with DXGI_PRESENT_DO_NOT_WAIT (a NON-primary target only - the primary's blocking present is what the
    // pacing is built around): a queue still full refuses the present (LastPresentRefused) instead of blocking the render
    // thread, nothing is queued, and the latency credit stays held.
    internal void Present(D3D12Swapchain target, bool noWait = false)
    {
        // Reset BEFORE the Disposed early-out: a dead target must never replay the previous call's refusal (the host would keep
        // the frame owed, and the shared render thread ticking, to retry a swapchain that is gone).
        target.Frame.LastPresentRefused = false;
        if (target.Disposed) return;
        AssertSubmitThread();   // seam Step 0: when render-confined (force-sync/async), only the render thread may present
        BeginTargetFrame(target.Frame);
        // Seam (DIM on-screen fix): bind the DirectComposition graph on the PRESENTING thread the first time this composited
        // swapchain presents — deferred out of the UI-thread InitSwapChain so DComp is owned by the thread that Presents.
        if (target.Composited && target.DcompBindPending) BindDComp(target);
        target.Frame.LastPresentStoodDown = false;
        target.Frame.LastPresentRefused = false;
        HWND hwnd = target.Hwnd;
        // The ATOMIC REVEAL frame is exempt from every stand-down below. A COMPOSITION swapchain
        // (CreateSwapChainForComposition — every windowed popup, see InitSwapChain) reaches the screen through its
        // composition visual, NOT through the HWND: its host window's visibility says nothing about whether this
        // present is legal or useful. And a windowed popup's HWND is deliberately created hidden and revealed only
        // once this first frame has landed (Win32PopupWindow ctor + AppHost's HasPresentedContent reveal gate) — so
        // standing down here DROPPED the popup's only content frame while the reveal went ahead anyway, leaving the
        // popup as its frosted composition chrome with nothing painted inside it (the "empty flyout").
        bool revealFrame = target.Composited && !target.HasPresentedContent;
        // Cloak / hidden / iconic: skip Present (no GPU). AppHost reads LastPresentStoodDown and applies the same pacing
        // floor as skip-submit so the sync path does not free-spin without a Present pacer.
        if (!revealFrame && IsHwndCovered(hwnd))
        {
            StandDownPresent(target.Frame);
            return;
        }
        // OCCLUDED latch: MUST probe with DXGI_PRESENT_TEST while latched — a full stand-down before Present would make
        // the clear-on-S_OK path unreachable and freeze the window forever after the first OCCLUDED. (The latch is
        // device-wide, so it must not swallow a different target's reveal frame either.)
        if (target.Frame.OccludedLatched && !revealFrame)
        {
            HRESULT test = (HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChainVtbl.Present(
                target.SwapChain, 0, DxgiPresentTest);
            if ((int)test == DxgiStatusOccluded) { StandDownPresent(target.Frame); return; }
            if ((int)test < 0)
            {
                uint reason = ((uint)test == 0x887A0005u || (uint)test == 0x887A0007u) ? (uint)_device->GetDeviceRemovedReason() : 0u;
                if (_signalDeviceLostInsteadOfThrow) { System.Threading.Volatile.Write(ref _deviceLostReason, reason != 0u ? (int)reason : (int)(uint)test); return; }
                throw new InvalidOperationException($"Present(TEST) failed: 0x{(uint)test:X8}" + (reason != 0u ? $" (device removed reason 0x{reason:X8})" : ""));
            }
            target.Frame.OccludedLatched = false;   // S_OK (or other success) — window is showable again; fall through to a real Present
        }
        // A keep-alive repaint fired from inside an OS modal move/size loop (host called SuppressVsyncOnce) presents at
        // SyncInterval 0 so the WndProc thread isn't blocked up to a vblank — the live-resize/move hitch. On the composited
        // DComp flip swapchain interval-0 is a cheap, tear-free hand-off (DWM still composites at vblank); steady-state
        // frames keep _vsync's interval. Self-resetting: one unsynced present per call. Per-TARGET now (Phase 1 §3.3) —
        // a detached child's interactive present no longer steals the main window's suppression (or vice versa).
        bool noVsync = target.Frame.SkipVsyncOnce; target.Frame.SkipVsyncOnce = false;
        // ALLOW_TEARING is valid only with sync-interval 0 on a tearing-capable (non-composited) swapchain; on the
        // composition path target.TearingSupported is false so the modal-tick present is interval 0 with flags 0 (valid + tear-free).
        uint interval = (_vsync && !noVsync) ? 1u : 0u;
        uint flags = (interval == 0 && target.TearingSupported) ? DXGI.DXGI_PRESENT_ALLOW_TEARING : 0u;
        // F085: a secondary target never blocks the shared render thread inside Present (the primary keeps its contract).
        if (noWait && !ReferenceEquals(target, _primarySwapchain)) flags |= DxgiPresentDoNotWait;
        // F070 Stage B: a frame that moves video geometry waits (bounded) for its own GPU work to retire BEFORE the present, so the flip
        // and the video Commit the host issues right after it reach DWM together. Armed per turn by the host, never on steady playback.
        if (target.Frame.HintMotionFenceWait)
        {
            target.Frame.HintMotionFenceWait = false;
            WaitMotionFence(target);
        }
        // Whole-frame Present: the swapchain is FLIP_DISCARD, which refuses partial presentation (Present1 with dirty
        // rects is DXGI_ERROR_INVALID_CALL — pinned by ComAbiBindingTests), and the composite rewrites the whole back
        // buffer every frame anyway (docs/plans/scroll-gpu-retained-tiles-implementation.md, P2 status).
        HRESULT pr;
        if (target.SequentialFlip && target.PpPresentCount > 0 && !target.PpNeedFullPresent)
        {
            // Partial: the staged dirty rects of the frame just composited (relative to the last presented frame — a
            // stood-down present clears the way with a whole one first, PpNeedFullPresent).
            fixed (RECT* rects = target.PpPresentRects)
            {
                DXGI_PRESENT_PARAMETERS pp = default;
                pp.DirtyRectsCount = (uint)target.PpPresentCount;
                pp.pDirtyRects = rects;
                pr = (HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChain1Vtbl.Present1(target.SwapChain, interval, flags, &pp);
            }
        }
        else
            pr = (HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChainVtbl.Present(target.SwapChain, interval, flags);   // GEN-COM (wired)
        // REFUSED (DXGI_ERROR_WAS_STILL_DRAWING, only possible with DO_NOT_WAIT): the queue was still full, so NOTHING was queued
        // and no credit was spent. Like a stand-down, return without touching LatencyCreditHeld - the slot this turn reserved is
        // still free, and the next submit must not wait for it twice. The frame sits drawn in the current back buffer (no flip
        // happened); the host owes it and re-presents it, or draws a newer one over it. The staged dirty rects described a flip
        // that did not happen, so the present that eventually runs is a WHOLE one (PpNeedFullPresent).
        if ((uint)pr == DxgiErrorWasStillDrawing)
        {
            target.Frame.LastPresentRefused = true;
            target.PpPresentCount = 0;
            target.PpNeedFullPresent = true;
            Diag.Count("d3d12", "presentWasStillDrawing");
            EndTargetFrame();
            return;
        }
        target.PpPresentCount = 0;
        target.PpNeedFullPresent = false;
        // The Present is what SPENDS the latency credit a wait took (the waitable is a semaphore: it is re-signaled when
        // this present retires from the queue). Cleared on every path where Present actually ran — success AND
        // DXGI_STATUS_OCCLUDED, which is a success code that still consumed the slot. The stand-down / PRESENT_TEST /
        // covered-HWND paths above returned WITHOUT presenting, so they deliberately leave the credit held: the slot they
        // reserved is still free and the next turn must not wait for it twice. Per target: a non-primary swapchain obeys
        // the same semaphore contract as the primary (see WaitForLatency, F107).
        if (target.LatencyCreditHeld) target.LatencyCreditHeld = false;
        // The present's OWN queue work (the flip; on WARP a copy task) runs after Present returns, and the stamp SubmitDrawList
        // wrote (LastSubmitFence) precedes it — so a teardown or resize that waited for that stamp alone could free this target's
        // back buffers under the present (the WARP test-host access violation, d3d10warp!Task_Copy under ~CDXGISwapChain).
        // Signal the device fence past the present and remember the value on THIS target; TargetFenceHorizon folds it in. Per
        // target, never a device-wide wait (F117). Skipped when the present failed: the device may be gone, and the recover path
        // drains everything.
        if ((int)pr >= 0 && _fence != null) target.Frame.LastPresentFence = SignalNext();
        // DXGI_STATUS_OCCLUDED (0x087A0001) is a SUCCESS code — previously dropped by the `< 0` check. Composition
        // swapchains often never return it; when they do, latch and stand down (next frame probes with PRESENT_TEST).
        if ((int)pr == DxgiStatusOccluded)
        {
            target.Frame.OccludedLatched = true;
            Diag.Count("d3d12", "presentOccluded");
            StandDownPresent(target.Frame);
            return;
        }
        if ((int)pr < 0)
        {
            // Surface GetDeviceRemovedReason on a device-removed/reset so a GPU fault names its cause instead of a bare
            // 0x887A0005 (the empirical probe for the popup-swapchain path).
            uint reason = ((uint)pr == 0x887A0005u || (uint)pr == 0x887A0007u) ? (uint)_device->GetDeviceRemovedReason() : 0u;
            // Step 4 (async): record the loss + bail instead of throwing on the render thread (unobserved bg exception =
            // process death). The UI's recover gate polls PollDeviceLost and drives RecoverDevice. Non-async: unchanged throw.
            if (_signalDeviceLostInsteadOfThrow) { System.Threading.Volatile.Write(ref _deviceLostReason, reason != 0u ? (int)reason : (int)(uint)pr); return; }
            throw new InvalidOperationException($"Present failed: 0x{(uint)pr:X8}" + (reason != 0u ? $" (device removed reason 0x{reason:X8})" : ""));
        }
        // F101: a settle hint armed for this present is NOT flushed here any more. The host commits the frame's video
        // placement right after Present returns, and a DwmFlush in between composed the new hole over the OLD video rect (one
        // DWM frame of trailing edge) while stalling the shared render thread for a refresh. D3D12Swapchain.CompleteSettlePresent
        // runs the sync once the placement is committed (inline present only; a threaded present never blocks).
        // This target's front buffer / composition surface now holds engine-drawn pixels. One-way latch, read by the
        // host's popup reveal gate (never reveal a popup window whose swapchain has painted nothing) and by its
        // "owes a first paint" wake reason. Set only on the fall-through success path — a stood-down or OCCLUDED
        // present returned above and presented nothing.
        target.NotePresentedContent();
        if (s_firstPresentQpc == 0 && ReferenceEquals(target, _primarySwapchain))
        {
            s_firstPresentQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            // ALWAYS-ON, exactly once: what the GPU-memory census is actually MADE OF at first pixel, by resource
            // class. `gpu bytes` alone (one total) cannot answer "which class holds it", and on a UMA adapter every
            // one of these classes is pinned host memory — i.e. working set — so the split is the only way to attribute
            // a resident-memory regression without a live capture. One line, one string, at one instant.
            FluentGpu.Foundation.Diag.Line("[d3d12.mem] first-present " + D3D12MemoryDiagnostics.BreakdownLine(10));
        }
        PublishVideoMemorySnapshot();
        // A Present that actually ran (the stand-down / TEST / covered paths returned above) feeds the attested ledger.
        // Every target's attested ledger is fed from ITS OWN swapchain's frame statistics (pacing evidence is per target); only the
        // primary also takes the 1 Hz DWM sample, which is main-monitor-global and would be counted twice from a second target.
        SamplePresentStats(target.Frame, ReferenceEquals(target, _primarySwapchain));
        EndTargetFrame();
    }

    void ReleaseAdapter3()
    {
        if (_adapter3 == null) return;
        _adapter3->Release();
        _adapter3 = null;
    }

    void EnsureAdapter3()
    {
        if (_adapter3 != null || _device == null || _factory == null) return;
        LUID luid = _device->GetAdapterLuid();
        IDXGIAdapter3* adapter = null;
        if ((int)_factory->EnumAdapterByLuid(luid, __uuidof<IDXGIAdapter3>(), (void**)&adapter) >= 0 && adapter != null)
            _adapter3 = adapter;
    }

    // The receipts UI reads this on a 5 s timer, so producing it every Present would spend two DXGI calls per frame to
    // refresh a value nobody looks at 299 times out of 300. Sample on a cold cadence instead: ~1 Hz at 60 fps. On the
    // Weak (UMA/iGPU) tier we sample ~6× faster (every 10 presents) so the host's VRAM-pressure eviction reacts within
    // ~160ms instead of ~1s — a weak part pages hard when usage exceeds its LOCAL budget (adreno-hang-fixes.md M5). On UMA
    // the LOCAL budget reads as the shared-pool residency budget (~15 GB), not the 128 MB carve-out, so that eviction only arms
    // under real system memory pressure (F251); the faster cadence is what makes it prompt when the OS shrinks the budget.
    const int VideoMemorySampleEveryNPresents = 60;
    const int VideoMemorySampleEveryNPresentsWeak = 10;
    int _videoMemorySampleCountdown;

    // Last sampled LOCAL (device-dedicated) segment usage/budget, cached for IGpuDevice.TryGetVramUsage. _vramSampled
    // stays false until the first successful QueryVideoMemoryInfo so callers can distinguish "unknown" from "0 used".
    long _vramUsedBytes, _vramBudgetBytes;
    bool _vramSampled;

    /// <inheritdoc/>
    public bool TryGetVramUsage(out long usedBytes, out long budgetBytes)
    {
        usedBytes = _vramUsedBytes;
        budgetBytes = _vramBudgetBytes;
        return _vramSampled;
    }

    // `force` bypasses the countdown entirely (no decrement, no reschedule) for the one early sample
    // EnsureDeviceCreated takes right after device bring-up — so budget derivation (GpuMemoryBudgets.For, the
    // image-cache cap's LOCAL-budget input) sees a real _vramSampled=true before the image cache is even built,
    // instead of waiting up to 10/60 presents for the ordinary cadence below to first fire.
    void PublishVideoMemorySnapshot(bool force = false)
    {
        if (!force)
        {
            if (--_videoMemorySampleCountdown > 0) return;
            _videoMemorySampleCountdown = GpuProfile.IsWeak ? VideoMemorySampleEveryNPresentsWeak : VideoMemorySampleEveryNPresents;
        }
        EnsureAdapter3();
        if (_adapter3 == null) return;
        DXGI_QUERY_VIDEO_MEMORY_INFO local = default, nonLocal = default;
        if ((int)_adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local) < 0) return;
        _ = _adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal);
        // Cache the LOCAL segment for the VRAM-pressure seam (long is ample — budgets are < 2^63).
        _vramUsedBytes = (long)local.CurrentUsage;
        _vramBudgetBytes = (long)local.Budget;
        _vramSampled = true;
        D3D12MemoryDiagnostics.PublishVideoMemory(
            local.CurrentUsage, local.Budget,
            nonLocal.CurrentUsage, nonLocal.Budget,
            _imageTextures?.AtlasImageCount ?? 0,
            _imageTextures?.AtlasPageCount ?? 0,
            _glyphs?.CachedGlyphCount ?? 0);
    }

    // DXGI_STATUS_OCCLUDED — Present succeeded but the window is fully occluded (HWND flip-model). Not reliably returned
    // for CreateSwapChainForComposition; cloak/visibility is the composition path's stand-down. DXGI_PRESENT_TEST = 1.
    private const int DxgiStatusOccluded = unchecked((int)0x087A0001);
    private const uint DxgiPresentTest = 1u;
    private const uint DxgiPresentDoNotWait = 0x00000008u;                 // DXGI_PRESENT_DO_NOT_WAIT
    private const uint DxgiErrorWasStillDrawing = 0x887A000Au;             // DXGI_ERROR_WAS_STILL_DRAWING
    private const uint DwmwaCloaked = 14;
    // OccludedLatched/LastPresentStoodDown MOVED to TargetFrameState (Phase 1 §3.3) — per-target now, not device-global.

    /// <inheritdoc/>
    public bool LastPresentStoodDown => _primarySwapchain!.Frame.LastPresentStoodDown;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attr, out int value, uint size);

    private static bool IsHwndCovered(HWND hwnd)
    {
        if (hwnd == HWND.NULL) return false;
        if (IsIconic(hwnd) != 0 || IsWindowVisible(hwnd) == 0) return true;
        return DwmGetWindowAttribute((nint)hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) >= 0 && cloaked != 0;
    }

    private void StandDownPresent(TargetFrameState f)
    {
        f.LastPresentStoodDown = true;
        // DWM never saw this frame's changes: the next present of a partial-present target must be whole.
        for (int i = 0; i < _swapchains.Count; i++)
            if (ReferenceEquals(_swapchains[i].Frame, f)) { _swapchains[i].PpNeedFullPresent = true; _swapchains[i].PpPresentCount = 0; }
        f.HintSettlePresent = false;
        f.HintMotionFenceWait = false;
        f.SkipVsyncOnce = false;
        EndTargetFrame();
    }

    // ── OS-attested present statistics (always on) ──────────────────────────────────────────────────────────────────
    // The in-app present stamp (AppHost.LastPresentQpc) is submit-confirmed: it says Present() returned, not that a
    // scanout began. These two OS sources are the vblank-attested truth that bounds it. Cost is one DXGI call per
    // present plus one DWM call per second — no queries, no allocation, nothing per-draw — so unlike the pass-granular timestamp timeline this
    // is safe to leave on while measuring the very cadence it reports.
    // LastPresentStats/LastDwmSampleQpc/DwmFrames*/DwmBaselined MOVED to TargetFrameState (Phase 1 §3.3) — the 1 Hz DWM
    // sample runs on whichever target presents (in practice only ever the primary — see the call site below), and the
    // counters are documented main-monitor-GLOBAL regardless, so no attribution is lost by keying them per-target.

    // Always-on PLAIN counters (deliberately NOT Diag.Count — Diag.* is [Conditional] and compiles out of Release,
    // Diag.cs:64-84; budgets.md "always-on plain counter" precedent). Accumulated ONLY in the 1 Hz DWM branch
    // (fresh deltas) ⇒ per-present cost zero; nothing allocates outside the 1 Hz / 60 s branches.
    private ulong _glitchDroppedTotal, _glitchMissedTotal, _glitchLateTotal;
    private long _glitchSampledSeconds;   // 1 Hz samples folded in ≈ seconds of PRESENTED time observed
    private long _lastGlitchReportQpc;
    private ulong _glitchDroppedAtReport, _glitchMissedAtReport, _glitchLateAtReport;
    private long _glitchSecondsAtReport;

    /// <inheritdoc/>
    public PresentStats LastPresentStats => _primarySwapchain!.Frame.LastPresentStats;

    // primary = false (a detached child / popup target): only the swapchain's own DXGI frame statistics feed its own PresentLedger.
    // The DWM sample, the glitch totals, the topology check and LastPresentStats are main-monitor-global / primary-window state and
    // stay the primary's alone.
    private void SamplePresentStats(TargetFrameState f, bool primary)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();   // ≈ this present's submit (Present just returned)
        DXGI_FRAME_STATISTICS fs = default;
        bool fsOk = false;
        uint presentId = 0;
        if (f.Target.SwapChain != null)
        {
            // DXGI_ERROR_FRAME_STATISTICS_DISJOINT (0x887A000A) on the first call and after every mode change: the
            // sequence restarted, so this sample cannot be differenced against the previous one. Swallow it — the next
            // call succeeds — but do NOT publish the garbage numbers as if they were data.
            fsOk = (int)f.Target.SwapChain->GetFrameStatistics(&fs) >= 0;
            // The id of the present that just ran, in the PresentCount domain: the ledger banks this present's idle
            // vblanks under it and credits them when a (lagging) sample finally shows it displayed.
            if ((int)f.Target.SwapChain->GetLastPresentCount(&presentId) < 0) presentId = 0;
        }
        // Attested displayed/dropped/repeated (plan §5.3): DXGI's paired counters only — a sample describes the last
        // DISPLAYED present and lags the submit, so it is never compared against a submitted count. Idle vblanks between
        // this submit and the previous one (counted on the vblank grid: DXGI's SyncQPCTime, else the DWM vblank) are not
        // repeats. Keyed on the frame-statistics validity alone — the 1 Hz DWM sample below is a separate source and must
        // not hold the ledger's baseline hostage for the first second; a DISJOINT sample resets the baseline.
        long vblankAnchor = fsOk && fs.SyncQPCTime != 0 ? fs.SyncQPCTime : f.LastPresentStats.VBlankQpc;
        // A non-primary target never assigns LastPresentStats (it returns below), so its idle-vblank period is its OWN monitor's, not that field's 0.
        long idlePeriod = primary ? f.LastPresentStats.RefreshPeriodQpc : ResolveTargetRefreshPeriod(f, now);
        uint idle = FluentGpu.Hosting.Threading.PresentStatisticsLedger.IdleRefreshes(f.LastSubmitQpc, now, vblankAnchor, idlePeriod);
        f.LastSubmitQpc = now;
        f.PresentLedger.Observe(fsOk && presentId != 0, fs.PresentCount, fs.PresentRefreshCount, presentId, idle);
        if (!primary) return;

        long refreshPeriod = f.LastPresentStats.RefreshPeriodQpc;
        long vblankQpc = f.LastPresentStats.VBlankQpc;
        uint dropped = 0, missed = 0, late = 0;
        bool dwmOk = f.DwmBaselined;
        if (now - f.LastDwmSampleQpc >= System.Diagnostics.Stopwatch.Frequency)
        {
            f.LastDwmSampleQpc = now;
            DWM_TIMING_INFO ti = default;
            ti.cbSize = (uint)sizeof(DWM_TIMING_INFO);
            // hwnd MUST be NULL: the per-window form was removed in Windows 8.1 and now returns E_INVALIDARG. These are
            // therefore main-monitor-GLOBAL counters, with no per-window attribution — a confound to record, not a fault
            // to assign.
            if ((int)DwmGetCompositionTimingInfo(HWND.NULL, &ti) >= 0)
            {
                refreshPeriod = (long)ti.qpcRefreshPeriod;
                vblankQpc = (long)ti.qpcVBlank;
                if (f.DwmBaselined)
                {
                    dropped = unchecked((uint)(ti.cFramesDropped - f.DwmFramesDropped));
                    missed = unchecked((uint)(ti.cFramesMissed - f.DwmFramesMissed));
                    late = unchecked((uint)(ti.cFramesLate - f.DwmFramesLate));
                    dwmOk = true;
                    f.DwmSampleSeq++;   // a fresh delta: readers sum each sample's deltas once, keyed on this id
                    _glitchDroppedTotal += dropped;
                    _glitchMissedTotal += missed;
                    _glitchLateTotal += late;
                    _glitchSampledSeconds++;
                }
                f.DwmFramesDropped = ti.cFramesDropped;
                f.DwmFramesMissed = ti.cFramesMissed;
                f.DwmFramesLate = ti.cFramesLate;
                f.DwmBaselined = true;
                if (_primarySwapchain is { } psc)
                {
                    SamplePresentTopology(psc);
                    // Per-window refresh period wins over the DWM-global sample above when known: the DWM counters
                    // are main-monitor-GLOBAL (comment at the top of this branch), but the frame pacer / present-time
                    // prediction / DirectManipulation lead / ambient FPS cap must all pace on THIS window's monitor.
                    if (psc.CachedRefreshPeriodQpc > 0) refreshPeriod = psc.CachedRefreshPeriodQpc;
                }
                MaybeReportGlitches(now);
            }
        }
        else
        {
            // No fresh DWM sample on this present: re-publish the latest sample's deltas under the SAME DwmSampleSeq, so a
            // reader that sums per present counts each sample once (PresentStats.DwmSampleSeq).
            dropped = f.LastPresentStats.DwmFramesDroppedDelta;
            missed = f.LastPresentStats.DwmFramesMissedDelta;
            late = f.LastPresentStats.DwmFramesLateDelta;
        }

        f.LastPresentStats = new PresentStats
        {
            Valid = fsOk && dwmOk && refreshPeriod > 0,
            PresentCount = fsOk ? fs.PresentCount : 0u,
            PresentRefreshCount = fsOk ? fs.PresentRefreshCount : 0u,
            SyncRefreshCount = fsOk ? fs.SyncRefreshCount : 0u,
            SyncQpc = fsOk ? fs.SyncQPCTime : 0L,
            RefreshPeriodQpc = refreshPeriod,
            VBlankQpc = vblankQpc,
            DwmFramesDroppedDelta = dropped,
            DwmFramesMissedDelta = missed,
            DwmFramesLateDelta = late,
            DwmSampleSeq = f.DwmSampleSeq,
            LatencyWaitMs = f.LastLatencyWaitMs,
        };
    }

    // A non-primary target's refresh period for the ledger's idle-vblank count: the swapchain's cached per-window period
    // (resolved at InitSwapChain by SamplePresentTopology), re-resolved at most ~1 Hz when the window moved monitors (cold path
    // only: DisplayInfo.ForWindow walks QueryDisplayConfig; no log line per popup target), else the primary's period.
    private long ResolveTargetRefreshPeriod(TargetFrameState f, long now)
    {
        D3D12Swapchain t = f.Target;
        if (t.Hwnd != HWND.NULL && now - f.LastDwmSampleQpc >= System.Diagnostics.Stopwatch.Frequency)
        {
            f.LastDwmSampleQpc = now;
            HMONITOR mon = MonitorFromWindow(t.Hwnd, MonitorDefaultToNearest);
            if (mon != HMONITOR.NULL && mon != t.TopologyMonitor)
            {
                var mode = FluentGpu.Pal.Windows.DisplayInfo.ForWindow((nint)t.Hwnd);
                if (mode.Valid)
                {
                    t.CachedRefreshPeriodQpc = mode.RefreshPeriodQpc;
                    t.TopologyMonitor = mon;
                }
            }
        }
        return t.CachedRefreshPeriodQpc > 0 ? t.CachedRefreshPeriodQpc : _primarySwapchain?.Frame.LastPresentStats.RefreshPeriodQpc ?? 0;
    }

    // Does the RENDER adapter own a DXGI output containing the window's monitor? When not, every present crosses
    // adapters (DWM cross-adapter scan-out): correct output, but an extra compositor copy + latency — the classic
    // "high FPS, bad feel" confound this line names. A dGPU with ZERO outputs presenting through the iGPU is the
    // designed-in shape of a hybrid laptop: a CONDITION to record, not an error. Runs at InitSwapChain and inside
    // the existing 1 Hz DWM branch only; HMONITOR early-out ⇒ steady state is one user32 call/s, zero DXGI calls.
    // Emits [d3d12.present] on a state CHANGE only — never per present.
    internal const int TopologyUnknown = 0, TopologyOwned = 1, TopologyCross = 2, TopologyNoOutputs = 3;
    private const uint MonitorDefaultToNearest = 2;   // MONITOR_DEFAULTTONEAREST — same local-const shape as Win32Platform.cs:380

    private void SamplePresentTopology(D3D12Swapchain target)
    {
        if (_device == null || _factory == null || target.Hwnd == HWND.NULL) return;
        HMONITOR mon = MonitorFromWindow(target.Hwnd, MonitorDefaultToNearest);
        if (mon == HMONITOR.NULL) return;
        bool firstResolve = target.PresentTopologyState == TopologyUnknown;
        if (mon == target.TopologyMonitor && !firstResolve) return;   // steady state

        // Per-window refresh period (mixed-refresh-rate audit): the DWM global counters this is called from
        // (SamplePresentStats) are main-monitor-GLOBAL — resolve THIS window's monitor mode on the SAME edge as the
        // topology check below (cold path only: DisplayInfo.ForWindow does the QueryDisplayConfig walk, never a
        // per-present/per-frame call). Compared against the primary monitor's rate so a mismatch is self-evident.
        var mode = FluentGpu.Pal.Windows.DisplayInfo.ForWindow((nint)target.Hwnd);
        var primaryMode = FluentGpu.Pal.Windows.DisplayInfo.ForPrimary();
        if (mode.Valid) target.CachedRefreshPeriodQpc = mode.RefreshPeriodQpc;

        LUID luid = _device->GetAdapterLuid();
        IDXGIAdapter1* adapter = null;
        if ((int)_factory->EnumAdapterByLuid(luid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) < 0 || adapter == null)
            return;   // stale factory mid-topology-change: keep old state; the next 1 Hz sample retries
        bool sawOutput = false, owns = false;
        FluentGpu.Media.VideoOverlayCaps overlay = default;   // F249: the owning output's overlay-plane verdict (default = unprobed)
        for (uint i = 0; ; i++)
        {
            IDXGIOutput* output = null;
            if ((int)adapter->EnumOutputs(i, &output) < 0 || output == null) break;
            DXGI_OUTPUT_DESC od = default;
            if ((int)output->GetDesc(&od) >= 0)
            {
                sawOutput = true;
                if (od.Monitor == mon) { owns = true; overlay = ProbeOverlaySupport(output); }
            }
            output->Release();
            if (owns) break;
        }
        adapter->Release();

        int state = owns ? TopologyOwned : sawOutput ? TopologyCross : TopologyNoOutputs;
        target.TopologyMonitor = mon;
        bool topologyChanged = state != target.PresentTopologyState;
        target.PresentTopologyState = state;
        bool overlayChanged = overlay != target.OverlayCaps;
        target.OverlayCaps = overlay;
        if (overlay.Probed) FluentGpu.Media.VideoOverlayCaps.Publish(in overlay);   // the engines read the newest verdict when they are created

        // Diagnostics-card snapshot (GpuVideoMemorySnapshot): merges the display-mode + topology fields without
        // touching the video-memory fields, which PublishVideoMemory refreshes on its own (~1/60-presents) cadence.
        // Refreshed on every edge reached here, independent of the "changed" filtering on the two Diag.Line calls
        // below — a settings page polling this struct must not see a stale monitor after a move-with-same-topology.
        D3D12MemoryDiagnostics.PublishDisplayMode(
            mode.Valid ? mode.DeviceName : null,
            mode.RefreshNumerator, mode.RefreshDenominator, mode.RefreshPeriodQpc,
            primaryMode.RefreshNumerator, primaryMode.RefreshDenominator,
            FRAME_COUNT, (uint)Volatile.Read(ref _presentQueueDepth), state, mode.Valid);

        if (mode.Valid)
        {
            double hz = mode.RefreshDenominator > 0 ? (double)mode.RefreshNumerator / mode.RefreshDenominator : 0.0;
            double primaryHz = primaryMode.Valid && primaryMode.RefreshDenominator > 0
                ? (double)primaryMode.RefreshNumerator / primaryMode.RefreshDenominator : 0.0;
            bool mismatch = primaryMode.Valid && Math.Abs(hz - primaryHz) > 0.01;
            Diag.Line($"[d3d12.display] monitor={mode.DeviceName} hz={hz:0.000} periodQpc={mode.RefreshPeriodQpc}" +
                      $" primaryHz={primaryHz:0.000} changed={(mismatch ? "true" : "false")}");
        }

        if (!topologyChanged && !overlayChanged) return;
        // F249: an owned output no longer claims a direct scan-out path from adapter ownership alone; the note is what CheckOverlaySupport said.
        Diag.Line($"[d3d12.present] topology={(state == TopologyOwned ? "render-adapter-owns-output"
                : state == TopologyCross ? "cross-adapter" : "render-adapter-has-no-outputs")}" +
            $" hwnd=0x{(nint)target.Hwnd:X}" +
            (state == TopologyOwned
                ? $" overlay[{overlay.Describe()}] note={OverlayNote(in overlay)}"
                : " note=presents-cross-adapters-via-DWM-(expected-on-hybrid-laptops;-adds-a-compositor-copy)"));
    }

    // Once per minute, ONLY when nonzero: silence is the healthy steady state. Normalized to PRESENTED time —
    // skip-submit idle frames never reach SamplePresentStats, so "sampled" seconds are the honest denominator.
    private void MaybeReportGlitches(long nowQpc)
    {
        if (_lastGlitchReportQpc == 0) { _lastGlitchReportQpc = nowQpc; return; }
        if (nowQpc - _lastGlitchReportQpc < 60 * System.Diagnostics.Stopwatch.Frequency) return;
        _lastGlitchReportQpc = nowQpc;
        ulong d = _glitchDroppedTotal - _glitchDroppedAtReport;
        ulong m = _glitchMissedTotal - _glitchMissedAtReport;
        ulong l = _glitchLateTotal - _glitchLateAtReport;
        long s = _glitchSampledSeconds - _glitchSecondsAtReport;
        _glitchDroppedAtReport = _glitchDroppedTotal; _glitchMissedAtReport = _glitchMissedTotal;
        _glitchLateAtReport = _glitchLateTotal; _glitchSecondsAtReport = _glitchSampledSeconds;
        if (d == 0 && m == 0 && l == 0) return;
        Diag.Line($"[d3d12.present] dwmGlitches dropped={d} missed={m} late={l} presentedSeconds={s}" +
                  " note=main-monitor-global-counters;-sampled-only-while-presenting");
    }

    // Replaces SignalFrame(frameIndex) (Phase 1 §3.4): stamps ONE monotonic value; the caller (SubmitDrawList) writes
    // it into both the target's own FenceValues[k] and the ring's Fence[slot] — this method no longer knows or cares
    // which ledger it feeds.
    private ulong SignalNext()
    {
        ulong v = ++_fenceValue;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.Signal(_queue, _fence, v), "queue.Signal");   // GEN-COM (wired)
        return v;
    }

    // Replaces WaitForFrame(frameIndex) (Phase 1 §3.4/§3.6 TargetFenceLedger): takes the fence VALUE to wait for
    // directly (0 = nothing in flight yet), so the same body serves a target's back-buffer wait (f.FenceValues[k])
    // and the ring's slot wait (_ring.Fence[slot]) — two independent ledgers instead of one shared by back-buffer
    // index, which is what let two windows presenting every turn wait on each other's previous submit (§1.6).
    private bool WaitForFenceValue(ulong v)
    {
        if (v == 0) return true;
        ulong completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (completed == ulong.MaxValue)
        {
            NoteRemovedFenceValue();
            return false;
        }
        if (completed >= v) return true;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.SetEventOnCompletion(_fence, v, (void*)_fenceEvent), "SetEventOnCompletion");   // GEN-COM (wired)
        if (_signalDeviceLostInsteadOfThrow) return WaitFenceEventBounded(v, forceResetOnStall: true);   // Step 4: no INFINITE hang on a lost device (async); frame fence gates presentation → force a clean reset on a hard stall
        uint wait = WaitForSingleObject(_fenceEvent, INFINITE);
        if (wait == 0xFFFFFFFFu) throw new InvalidOperationException("WaitForSingleObject(frame fence) failed.");
        completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (completed == ulong.MaxValue)
        {
            NoteRemovedFenceValue();
            return false;
        }
        return completed >= v;
    }

    // Block until THIS target is ready to accept a new frame (bounds present-queue depth → lower latency, efficient
    // wait). Bounded timeout so a lost device can't hang the loop. No-op if the waitable wasn't created (older DXGI /
    // failure). Takes the target explicitly now (Phase 1 §3.3) — was a device-global Activate-mirrored field; a
    // detached child's own waitable is on `sc`, never the primary's.
    // The wait takes ONE semaphore count, i.e. a credit that exactly one Present spends: it is therefore HELD on the target
    // (success or the 1 s liveness timeout, the same contract as TryTakePresentSlot(-1)) until a Present actually runs, and
    // SubmitDrawList skips this wait while it is held. Without that, a submit whose Present stood down (cloaked / hidden /
    // OCCLUDED-latched HWND) left the count at 0 and every later submit on that target blocked the whole 1000 ms bound.
    // INTERRUPTIBLE (F207): a park request from the UI (SetSubmitAbortHandle) ends the wait with nothing taken. The credit then
    // stays un-held - claiming one that was never taken is the F090-style drift - and the submit goes on without paying the
    // wait (a stood-down Present spends nothing, a real one is bounded by DXGI's own queue).
    internal void WaitForLatency(D3D12Swapchain sc)
    {
        if (!sc.HasLatencyWaitable) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        LatencyWait wait = WaitLatencyOrAbort(sc.FrameLatencyWaitable, 1000);
        if (wait == LatencyWait.Aborted) return;
        sc.LatencyCreditHeld = true;
        if (!ReferenceEquals(sc, _primarySwapchain)) NoteNonPrimaryLatencyWait(sc, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds, wait == LatencyWait.Opened);
    }

    // The render loop's park-request event (RenderThread._parkRequested), handed in through SetSubmitAbortHandle. 0 = none
    // wired: every wait is then the plain bounded wait. Read per wait on the render thread; written by the host only before
    // the loop starts and after it was joined, so it never changes under a wait. The device waits on it, never closes it.
    private nint _submitAbortHandle;

    /// <inheritdoc/>
    public void SetSubmitAbortHandle(nint handle) => Volatile.Write(ref _submitAbortHandle, handle);

    /// <inheritdoc/>
    /// <remarks>The compositor creates its DispatcherQueue on the CURRENT thread, and the composition batches commit only
    /// when that thread pumps messages. Brought up by the first acrylic popup's swapchain on the render thread (which never
    /// pumps), every popup's chrome and motion would stay uncommitted.</remarks>
    public void PrepareSwapchainCreate(in SwapchainDesc desc)
    {
        if (desc.DesktopAcrylic) CompositionBackdrop.EnsureCompositor();
    }

    /// <summary>How a latency-waitable wait ended: the slot opened (one semaphore count taken), the bound passed (nothing
    /// taken), or the park-request event fired (nothing taken).</summary>
    private enum LatencyWait : byte { Opened, TimedOut, Aborted }

    /// <summary>Wait at most <paramref name="timeoutMs"/> for <paramref name="waitable"/> OR the park-request event. The
    /// waitable is index 0: when both are signaled WaitForMultipleObjects (bWaitAll = FALSE) reports the LOWEST index, so an
    /// open slot still wins and is the only outcome that took a count - an abort alone never reads as credit held. A failed
    /// wait (a torn-down handle) reads as a timeout, as it always did. Render thread only; no allocation.</summary>
    private LatencyWait WaitLatencyOrAbort(HANDLE waitable, uint timeoutMs)
    {
        nint abort = Volatile.Read(ref _submitAbortHandle);
        if (abort == 0) return WaitForSingleObject(waitable, timeoutMs) == WAIT_OBJECT_0 ? LatencyWait.Opened : LatencyWait.TimedOut;
        HANDLE* handles = stackalloc HANDLE[2];
        handles[0] = waitable;
        handles[1] = (HANDLE)abort;
        uint r = WaitForMultipleObjects(2, handles, false, timeoutMs);
        return r == WAIT_OBJECT_0 ? LatencyWait.Opened : r == WAIT_OBJECT_0 + 1 ? LatencyWait.Aborted : LatencyWait.TimedOut;
    }

    // Non-primary latency waits run on the shared render thread, so a long one is a main-window stall attributable to a
    // popup / pop-out. Logged rate-limited per target (the evidence for the next 1000 ms-class stall report).
    private const double NonPrimaryLatencyLogFloorMs = 20.0;
    private const double NonPrimaryLatencyLogPeriodMs = 2000.0;

    // F235: the always-on numbers behind that log, for the [render.pace] line: the window's longest non-primary wait (childWaitMax=) and
    // the cumulative count of waits that ran out their bound (timeoutTarget=child). Written by the submitting thread only; read by the
    // render thread's pace report (a torn-free long each).
    private long _nonPrimaryWaitMaxUs, _nonPrimaryTimeouts;

    /// <inheritdoc/>
    public long NonPrimaryLatencyTimeouts => Volatile.Read(ref _nonPrimaryTimeouts);

    /// <inheritdoc/>
    public double NonPrimaryLatencyWaitMaxMs => Volatile.Read(ref _nonPrimaryWaitMaxUs) / 1000.0;

    /// <inheritdoc/>
    public void ResetNonPrimaryLatencyWindow() => Volatile.Write(ref _nonPrimaryWaitMaxUs, 0);

    private void NoteNonPrimaryLatencyWait(D3D12Swapchain sc, double waitedMs, bool opened)
    {
        long waitedUs = (long)(waitedMs * 1000.0);
        if (waitedUs > Volatile.Read(ref _nonPrimaryWaitMaxUs)) Volatile.Write(ref _nonPrimaryWaitMaxUs, waitedUs);
        if (!opened) Volatile.Write(ref _nonPrimaryTimeouts, _nonPrimaryTimeouts + 1);
        if (opened && waitedMs < NonPrimaryLatencyLogFloorMs) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (sc.LatencyWaitLogQpc != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(sc.LatencyWaitLogQpc, now).TotalMilliseconds < NonPrimaryLatencyLogPeriodMs)
        {
            sc.LatencyWaitsSuppressed++;
            return;
        }
        Diag.Line($"[d3d12.present] non-primary latency wait target={sc.Ordinal} waitedMs={waitedMs:F1} timedOut={(opened ? 0 : 1)} suppressed={sc.LatencyWaitsSuppressed}");
        sc.LatencyWaitLogQpc = now;
        sc.LatencyWaitsSuppressed = 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The PRIMARY swapchain's present slot, waited for BEFORE the host picks which published frame to present, so the
    /// frame that reaches the glass is the freshest one that existed when the slot opened (this is the ordering
    /// Windows Terminal's AtlasEngine and makepad both use; acquiring first and waiting inside submit ages the acquired
    /// frame by the whole wait). The waitable is a SEMAPHORE: one successful wait == one credit that exactly one
    /// Present spends, so a second wait before that Present would block for a full extra present cycle. The credit is
    /// therefore tracked on the swapchain (<c>LatencyCreditHeld</c>): SubmitDrawList skips its own wait while it is
    /// held, Present clears it, and a turn that ends up presenting nothing simply keeps it (the slot really is still
    /// free). Reads the primary swapchain's handle directly rather than the Activate-mirrored device fields — the
    /// active target may be a popup.
    /// <para><b>Bounded take.</b> A clock-paced render turn asks with a short grace (<c>SlotCatchUp.GraceMs</c>): a slot
    /// still busy after it means the previous present missed its vblank and owns this one, and the caller may skip the
    /// tick rather than queue behind it. A timed-out wait took NOTHING (the semaphore count is untouched), so false needs
    /// no undo and the credit stays un-held. The bound is kept by a high-resolution timer (<see cref="WaitForSlotWithin"/>):
    /// a plain wait timeout is as coarse as the process timer resolution. <paramref name="timeoutMs"/> &lt; 0 is the
    /// liveness-bounded wait every other turn uses: it proceeds after 1 s even when the slot never opened (a lost device
    /// must not wedge the loop) and holds the credit either way — exactly the unbounded contract this replaced.</para>
    /// <para><b>Interruptible.</b> Every form of the wait also waits on the park-request event
    /// (<see cref="SetSubmitAbortHandle"/>), so the UI's rendezvous never waits out the 1 s bound. An interrupted take returns
    /// false in BOTH forms, takes nothing and leaves the credit un-held; the liveness form's caller must treat that false as
    /// "park now", not as a slot to retake.</para>
    /// </remarks>
    public bool TryTakePresentSlot(int timeoutMs)
    {
        AssertSubmitThread();   // seam Step 0: this is the render thread's pacing wait, never the UI's
        return _primarySwapchain is not { } sc || TryTakeLatencyCredit(sc, timeoutMs);
    }

    /// <summary>The per-target seam form: <paramref name="target"/>'s own credit (a detached pop-out's swapchain), so the
    /// shared render thread can probe a secondary window's slot with a 0 ms take instead of blocking on its vblank. A target
    /// this device did not create (a fake / disposed one) has no waitable here: reported as held.</summary>
    public bool TryTakePresentSlot(ISwapchain target, int timeoutMs)
    {
        AssertSubmitThread();
        return target is not D3D12Swapchain sc || TryTakeLatencyCredit(sc, timeoutMs);
    }

    /// <summary>The per-swapchain half of <see cref="TryTakePresentSlot"/>: take <paramref name="sc"/>'s present-slot credit
    /// (waiting at most <paramref name="timeoutMs"/>, -1 = the 1 s liveness bound). True when the credit is held (or the
    /// swapchain has no latency waitable); false when a bounded take did not get the slot. Render thread only.</summary>
    internal bool TryTakeLatencyCredit(D3D12Swapchain sc, int timeoutMs)
    {
        if (sc.Disposed || !sc.HasLatencyWaitable || sc.LatencyCreditHeld) return true;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // Liveness form: -1 = the 1 s default bound, -N = the same proceed-regardless wait bounded at N ms (an unpaced turn's
        // max(2 x refresh, 34 ms), F208).
        uint livenessMs = timeoutMs == -1 ? 1000u : timeoutMs < 0 ? (uint)(-(long)timeoutMs) : 0u;
        LatencyWait wait = timeoutMs < 0
            ? WaitLatencyOrAbort(sc.FrameLatencyWaitable, livenessMs)   // bounded: a lost device must not wedge the loop
            : WaitForSlotWithin(sc.FrameLatencyWaitable, timeoutMs);
        sc.Frame.LastLatencyWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        // A park request ended the wait: nothing was taken, whatever the form - the credit stays un-held and the caller parks.
        if (wait == LatencyWait.Aborted) return false;
        if (timeoutMs < 0 && wait == LatencyWait.TimedOut) NoteSlotLivenessTimeout(sc, livenessMs);
        // A bounded take that did not get the slot (timed out, or the wait failed on a torn-down handle) reserved nothing:
        // report it and leave the credit un-held. The liveness path keeps today's semantics and proceeds regardless.
        if (wait != LatencyWait.Opened && timeoutMs >= 0) return false;
        sc.LatencyCreditHeld = true;
        return true;
    }

    // F208 A1: a liveness-bounded take that ran out its bound without the slot ever opening (the presents of a minimized /
    // cloaked / occluded primary never retire, a queue-depth change left frames queued, ...). Counted always; one rate-limited
    // always-on line says what the primary looked like at that moment, so the cause is read off the log instead of guessed.
    // Render thread writes; SlotLivenessTimeouts is read from any thread.
    private long _slotLivenessTimeouts, _slotLivenessLogQpc, _slotLivenessSuppressed;
    private const double SlotLivenessLogPeriodMs = 2000.0;

    /// <inheritdoc/>
    public long SlotLivenessTimeouts => Volatile.Read(ref _slotLivenessTimeouts);

    private void NoteSlotLivenessTimeout(D3D12Swapchain sc, uint boundMs)
    {
        // The primary's timeouts are SlotLivenessTimeouts; a secondary swapchain's (reachable only through the per-target take) belong to
        // NonPrimaryLatencyTimeouts, so the pace line's timeoutTarget= can say whose slot never opened (F235).
        if (ReferenceEquals(sc, _primarySwapchain)) Volatile.Write(ref _slotLivenessTimeouts, _slotLivenessTimeouts + 1);
        else Volatile.Write(ref _nonPrimaryTimeouts, _nonPrimaryTimeouts + 1);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_slotLivenessLogQpc != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_slotLivenessLogQpc, now).TotalMilliseconds < SlotLivenessLogPeriodMs)
        {
            _slotLivenessSuppressed++;
            return;
        }
        HWND hwnd = sc.Hwnd;
        bool iconic = hwnd != HWND.NULL && IsIconic(hwnd) != 0;
        bool visible = hwnd == HWND.NULL || IsWindowVisible(hwnd) != 0;
        int cloaked = 0;
        if (hwnd != HWND.NULL) _ = DwmGetWindowAttribute((nint)hwnd, DwmwaCloaked, out cloaked, sizeof(int));
        uint lastPresentId = 0, statsPresentCount = 0;
        if (sc.SwapChain != null)
        {
            if ((int)sc.SwapChain->GetLastPresentCount(&lastPresentId) < 0) lastPresentId = 0;
            DXGI_FRAME_STATISTICS fs = default;
            if ((int)sc.SwapChain->GetFrameStatistics(&fs) >= 0) statsPresentCount = fs.PresentCount;
        }
        double sinceLastPresentMs = sc.Frame.LastSubmitQpc == 0 ? -1.0
            : System.Diagnostics.Stopwatch.GetElapsedTime(sc.Frame.LastSubmitQpc, now).TotalMilliseconds;
        Diag.Line($"[d3d12.present] slot liveness timeout target={sc.Ordinal} boundMs={boundMs} iconic={(iconic ? 1 : 0)} visible={(visible ? 1 : 0)} cloaked={cloaked} occludedLatched={(sc.Frame.OccludedLatched ? 1 : 0)} depth={Volatile.Read(ref _presentQueueDepth)} lastPresentId={lastPresentId} statsPresentCount={statsPresentCount} sinceLastPresentMs={sinceLastPresentMs:F1} total={_slotLivenessTimeouts} suppressed={_slotLivenessSuppressed}");
        _slotLivenessLogQpc = now;
        _slotLivenessSuppressed = 0;
    }

    // F070 Stage B: the bound a geometry-motion present waits for its own submit's fence (about one 60 Hz refresh). Past it the frame
    // presents anyway - one skewed frame, counted - so a slow frame can never stall the shared render thread.
    private const double MotionFenceCapMs = 16.0;

    // Render thread. Polls the fence (no event: a timed-out SetEventOnCompletion would leave a stale signal for the next real wait on
    // the shared fence event) and yields, so a frame that is nearly done costs the remainder of its GPU time, never more than the cap.
    private void WaitMotionFence(D3D12Swapchain target)
    {
        ulong v = target.Frame.LastSubmitFence;
        if (v == 0 || _fence == null) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // GetCompletedValue is UINT64_MAX on a removed device, which ends the wait: the present then reports the loss as usual.
        while (global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence) < v)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= MotionFenceCapMs)
            {
                Diag.Count("d3d12", "motionFenceTimeout");
                return;
            }
            System.Threading.Thread.Yield();
        }
        Diag.Count("d3d12", "motionFenceWait");
    }

    // F080: edits the video presenters applied to the shared IDCompositionDevice that no Commit has flushed yet. Every
    // presenter shares the device's one IDCompositionDevice, so ONE commit per render turn (CommitVideoComposition) flushes the
    // parent's and every pop-out's tree together instead of one commit per window landing in different DWM frames.
    private bool _videoCommitDue;
    private uint _videoCommitFailedHr;   // the last failing HRESULT logged (one line per distinct value)

    /// <summary>A presenter applied composition edits that need a device commit (render thread).</summary>
    internal void NoteVideoCommitDue() => _videoCommitDue = true;

    /// <inheritdoc/>
    public void CommitVideoComposition()
    {
        AssertSubmitThread();
        if (!_videoCommitDue) return;
        _videoCommitDue = false;
        if (_dcomp == null) return;
        HRESULT hr = _dcomp->Commit();   // non-throwing, like the presenter's own edits: a failure leaves DWM on the prior composition
        if ((int)hr < 0 && _videoCommitFailedHr != (uint)hr)
        {
            _videoCommitFailedHr = (uint)hr;
            Diag.Line($"[video.presenter] device Commit failed: 0x{(uint)hr:X8}");
        }
    }

    // TryTakePresentSlot's grace timer (render-thread owned; created on the first bounded take, closed in Dispose). A plain
    // wait timeout is only as fine as the process's timer resolution — 15.6 ms by default, and nothing in the engine
    // raises it — so a 2 ms grace on WaitForSingleObject can stretch past the ~8.5 ms retire it exists to detect: the slot
    // would open inside the "grace" and the catch-up would never fire. A HIGH-RESOLUTION waitable timer (Windows 10 1803+)
    // bounds the wait to the requested milliseconds — the same timer Win32Platform's display-rate waits use for the same
    // reason. Unavailable ⇒ the coarse timeout (the catch-up then engages less, never wrongly).
    private HANDLE _slotGraceTimer;
    private bool _slotGraceTimerUnavailable;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_MODIFY_STATE = 0x0002;
    private const uint SYNCHRONIZE = 0x00100000;

    /// <summary>The bounded half of <see cref="TryTakePresentSlot"/>: wait for the latency waitable, the one-shot grace
    /// timer OR the park-request event, whichever fires first. <see cref="LatencyWait.Opened"/> ⇔ the waitable satisfied the
    /// wait (one semaphore count taken); a timer or abort win took nothing (WaitForMultipleObjects with bWaitAll = FALSE
    /// changes only the object that satisfied it). The waitable is index 0 so a slot that opens together with the timer or
    /// the abort still wins. Render thread only; no allocation.</summary>
    private LatencyWait WaitForSlotWithin(HANDLE waitable, int timeoutMs)
    {
        nint abort = Volatile.Read(ref _submitAbortHandle);
        if (timeoutMs > 0 && TryEnsureSlotGraceTimer())
        {
            // Consume a stale signal first: a previous take the waitable won left the timer armed, and it may have fired
            // since (a synchronization timer stays signaled until a wait takes it) — that must not end THIS grace early.
            WaitForSingleObject(_slotGraceTimer, 0);
            LARGE_INTEGER due;
            due.QuadPart = -(long)timeoutMs * 10_000L;   // relative, 100 ns units
            if (SetWaitableTimer(_slotGraceTimer, &due, 0, null, null, false))
            {
                HANDLE* handles = stackalloc HANDLE[3];
                handles[0] = waitable;
                handles[1] = _slotGraceTimer;
                uint count = 2;
                if (abort != 0) handles[count++] = (HANDLE)abort;
                // The coarse timeout only backstops a timer that never fires.
                uint r = WaitForMultipleObjects(count, handles, false, (uint)timeoutMs + 16);
                if (r != WAIT_FAILED)
                    return r == WAIT_OBJECT_0 ? LatencyWait.Opened : r == WAIT_OBJECT_0 + 2 && abort != 0 ? LatencyWait.Aborted : LatencyWait.TimedOut;
            }
            CloseHandle(_slotGraceTimer);   // the timer failed once: stop using it (the coarse timeout below still bounds)
            _slotGraceTimer = HANDLE.NULL;
            _slotGraceTimerUnavailable = true;
        }
        return WaitLatencyOrAbort(waitable, (uint)timeoutMs);
    }

    private bool TryEnsureSlotGraceTimer()
    {
        if (_slotGraceTimer != HANDLE.NULL) return true;
        if (_slotGraceTimerUnavailable) return false;
        HANDLE timer = CreateWaitableTimerExW(null, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE);
        if (timer == HANDLE.NULL) { _slotGraceTimerUnavailable = true; return false; }
        _slotGraceTimer = timer;
        return true;
    }

    // One [d3d12.present] contract line per PROCESS (not per swapchain — every popup target takes the same const,
    // so a per-target line would be pure chatter in a session with many flyouts).
    private static bool s_loggedPresentContract;

    // _skipLatencyOnce/_lastFenceWaitMs/_lastLatencyWaitMs MOVED to TargetFrameState (Phase 1 §3.3) — per-target now
    // (SkipLatencyOnce/LastFenceWaitMs/LastLatencyWaitMs), read through `f.` inside SubmitDrawList and through
    // `_primarySwapchain!.Frame.X` from the IGpuDevice getters below (outside any submit).
    /// <inheritdoc/>
    public double LastFenceWaitMs => _primarySwapchain!.Frame.LastFenceWaitMs;
    /// <summary>Diagnostic: of <see cref="LastFenceWaitMs"/>, the frame-latency-waitable portion alone — compositor/present
    /// backpressure rather than command execution. Always measured (no env gate); the <c>[fps]</c> line's <c>latW</c> token.</summary>
    public double LastLatencyWaitMs => _primarySwapchain!.Frame.LastLatencyWaitMs;

    /// <inheritdoc/>
    public int MaxFrameLatency => Volatile.Read(ref _presentQueueDepth);

    /// <inheritdoc/>
    /// <remarks>Applies <c>IDXGISwapChain2::SetMaximumFrameLatency</c> to the primary swapchain (the waitable swapchain's
    /// queue depth may change at any time). The latency waitable is a semaphore whose capacity is the depth: a held
    /// credit (<see cref="TryTakePresentSlot"/>) stays valid across the change — it is still spent by exactly one
    /// Present — so the change takes effect on the next wait. Render thread only (the submit/present owner).</remarks>
    public int SetPresentQueueDepth(int depth)
    {
        AssertSubmitThread();
        int want = Math.Clamp(depth, (int)InitialPresentQueueDepth, (int)MaxPresentQueueDepth);
        if (want == Volatile.Read(ref _presentQueueDepth)) return want;
        if (_primarySwapchain is not { } sc || sc.Disposed || sc.SwapChain == null) return Volatile.Read(ref _presentQueueDepth);
        if (sc.SwapChain->SetMaximumFrameLatency((uint)want) < 0) return Volatile.Read(ref _presentQueueDepth);
        Volatile.Write(ref _presentQueueDepth, want);
        return want;
    }

    /// <inheritdoc/>
    /// <remarks>The per-swapchain form. The primary swapchain takes the device-level path above (it owns
    /// <c>_presentQueueDepth</c>, which feeds <c>MaxFrameLatency</c>); any other target this device created gets its OWN
    /// <c>SetMaximumFrameLatency</c> and its own stored depth, so a pop-out's GPU samples never retarget the main window's
    /// queue. A target this device did not create (a fake) is unchanged. Render thread only.</remarks>
    public int SetPresentQueueDepth(ISwapchain target, int depth)
    {
        AssertSubmitThread();
        if (target is not D3D12Swapchain sc) return Volatile.Read(ref _presentQueueDepth);
        if (ReferenceEquals(sc, _primarySwapchain)) return SetPresentQueueDepth(depth);
        int have = Volatile.Read(ref sc.PresentQueueDepth);
        int want = Math.Clamp(depth, (int)InitialPresentQueueDepth, (int)MaxPresentQueueDepth);
        if (want == have || sc.Disposed || sc.SwapChain == null) return have;
        if (sc.SwapChain->SetMaximumFrameLatency((uint)want) < 0) return have;
        Volatile.Write(ref sc.PresentQueueDepth, want);
        return want;
    }

    // ── Always-on whole-frame GPU execution timer ────────────────────────────────────────────────────────────────────
    // Exactly two TIMESTAMP queries per submitted command list, banked by RING SLOT (Phase 1 §3.3 — was banked by
    // back-buffer index; the pending/owner/ownerSubmit arrays moved to `_ring`, a CPU-written bank exactly like the
    // allocators). Their resolve is read only after the ring's own fence wait (in SubmitDrawList) proves that slot's
    // prior work retired. This is policy input, so it must exist independently of the optional pass-granular timeline
    // (GpuPassTimingEnabled): a CPU fence wait is not a substitute for on-GPU execution time. Creation failure is a supported state
    // (sequence stays 0); the adaptive governor then remains disengaged.
    private const uint GpuExecutionTsPerFrame = 2;
    private ID3D12QueryHeap* _gpuExecutionQueryHeap;
    private ID3D12Resource* _gpuExecutionTsReadback;
    private ulong* _gpuExecutionTsData;
    private ulong _gpuExecutionTsFreq;
    private bool _gpuExecutionTimingInitTried;

    // ── Pass-granular GPU timeline (runtime toggle: GpuPassTimingEnabled) ────────────────────────────────────────────
    // Timestamps ONLY at pass boundaries — frame start, after the uploads, after a baked-blur job, after the glyph band,
    // after the tile rasters, after the offscreen work (degraded chunks, groups, self-blur, backdrops), after the
    // back-buffer composite pass (or the direct route's transition + clear), frame end — never
    // between two draws into the same target (on a tiler that would itself end the pass being measured). Every
    // interval between two boundaries carries a (GpuPassKind, target px) tag written at record time into the ring
    // slot's side bank (SubmissionRing.PassKind/PassW/PassH); the interval between boundaries while drawing is
    // GpuPassKind.Scene tagged with the render target bound then. Read back one submission later, after the ring slot's
    // own fence wait proves the resolved timestamps retired (the CollectGpuExecutionTime pattern), and published per
    // target through the swapchain's seqlock (D3D12Swapchain.CopyGpuPassTimeline). Bounded at GpuPassTimeline.MaxPasses
    // intervals — a boundary past that is refused, so the following work folds into the interval still open, and is
    // counted (PassesDropped). Zero managed allocation per frame; the query heap + readback are created lazily the
    // first time the toggle is on. A submit recorded with the toggle off emits no queries and publishes nothing.
    private const uint PassTsPerSlot = GpuPassTimeline.MaxPasses + 2;   // ≤ MaxPasses + 1 marks used per frame
    private volatile bool _passTimingEnabled;
    private bool _passOn;                   // latched for the submit in progress (toggle on AND the heap exists)
    private ID3D12QueryHeap* _passQueryHeap;
    private ID3D12Resource* _passTsReadback;
    private ulong* _passTsData;
    private ulong _passTsFreq;
    private bool _passInitTried;
    private int _passMarks;                 // timestamps emitted this submit (the open interval is _passMarks - 1)
    private int _passDropped;
    private GpuPassKind _passKind;          // tag of the interval currently open
    private int _passW, _passH;
    private readonly GpuPassTiming[] _passScratch = new GpuPassTiming[GpuPassTimeline.MaxPasses];

    // ── Always-on per-submit device counters (published as GpuFrameCounters; plain fields, never Diag.*) ─────────────
    // _framePassBreaks: every pass boundary the submit crossed, counted whether or not the timeline is on — a
    // render-target switch (a tile raster, an offscreen surface, the back-buffer composite pass) or a copy/clear between
    // two runs of draws into the same target. Each one ends a render
    // pass on a tiling GPU (the load/store the timeline measures). The frame-start/upload/clear boundaries are included.
    private int _framePassBreaks;
    private int _frameBackBufferTransitions; // Barrier() calls on this submit's back buffer
    private ID3D12Resource* _frameBackBuffer;
    private int _frameImageUploads;
    private long _frameUploadBytes;
    private int _frameBakedBlurJobs;

    /// <inheritdoc/>
    /// <remarks>Latched by the render thread once per submit; settable from any thread. The first submit with it on
    /// creates the query heap + readback (a failed create leaves it permanently unavailable — the toggle then has no
    /// effect).</remarks>
    public bool GpuPassTimingEnabled
    {
        get => _passTimingEnabled;
        set => _passTimingEnabled = value;
    }

    private volatile int _knockouts;
    private GpuKnockouts _frameKnockouts;   // latched for the submit in progress (None on a popup target)
    /// <inheritdoc/>
    /// <remarks>Read once per submit, applied to the PRIMARY swapchain only (popups always render faithfully).</remarks>
    public GpuKnockouts Knockouts
    {
        get => (GpuKnockouts)_knockouts;
        set => _knockouts = (int)value;
    }

    private void EnsurePassTiming()
    {
        if (_passInitTried) return;
        _passInitTried = true;
        ulong freq;
        if (_queue == null || _queue->GetTimestampFrequency(&freq) < 0 || freq == 0) return;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = PassTsPerSlot * SubmissionRing.Depth;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (ulong)(PassTsPerSlot * SubmissionRing.Depth) * sizeof(ulong);
        rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null,
            __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        {
            heap->Release();
            return;
        }
        void* mapped;
        if (readback->Map(0, null, &mapped) < 0) { readback->Release(); heap->Release(); return; }
        _passQueryHeap = heap;
        _passTsReadback = readback;
        _passTsData = (ulong*)mapped;
        _passTsFreq = freq;
        D3D12MemoryDiagnostics.Track(readback, "GpuPassTimeline.TimestampReadback", rd.Width);
    }

    /// <summary>Frame start: latch the toggle for this submit and open the first interval.</summary>
    private void PassBegin(GpuPassKind first, int w, int h)
    {
        _passMarks = 0;
        _passDropped = 0;
        _passOn = false;
        if (!_passTimingEnabled) return;
        EnsurePassTiming();
        if (_passQueryHeap == null) return;
        _passOn = true;
        _cmdList->EndQuery(_passQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, PassTsPerSlot * (uint)_ringSlot);
        _passMarks = 1;
        _passKind = first; _passW = w; _passH = h;
    }

    /// <summary>A pass boundary: closes the interval open so far (its tag was set when it opened) and opens one tagged
    /// <paramref name="next"/> / <paramref name="w"/>×<paramref name="h"/> px. Counted as a pass break always; stamped
    /// only while the timeline is on for this submit. Call ONLY between passes — never between two draws into the same
    /// target.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PassBoundary(GpuPassKind next, int w, int h)
    {
        _framePassBreaks++;
        if (!_passOn) return;
        int m = _passMarks;
        if (m >= GpuPassTimeline.MaxPasses) { _passDropped++; return; }   // fold: the open interval keeps running
        int slot = _ringSlot;
        _ring.PassKind[slot][m - 1] = _passKind;
        _ring.PassW[slot][m - 1] = _passW;
        _ring.PassH[slot][m - 1] = _passH;
        _cmdList->EndQuery(_passQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, PassTsPerSlot * (uint)slot + (uint)m);
        _passMarks = m + 1;
        _passKind = next; _passW = w; _passH = h;
    }

    /// <summary>Corrects the target size of the interval still OPEN (its tag is written when it closes) — for a lease
    /// whose actual target size is known only once it succeeded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PassRetag(int w, int h) { _passW = w; _passH = h; }

    /// <summary>A boundary into scene drawing on the render target bound NOW.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PassToScene()
    {
        CurrentPassTargetSize(out int w, out int h);
        PassBoundary(GpuPassKind.Scene, w, h);
    }

    /// <summary>The physical size of the render target drawing currently lands in.</summary>
    private void CurrentPassTargetSize(out int w, out int h)
    {
        w = TargetW; h = TargetH;
    }

    /// <summary>Frame end: close the last interval, resolve this slot's marks and remember the tags for the
    /// one-submission-later readback.</summary>
    private void PassEnd(D3D12Swapchain sc)
    {
        if (!_passOn) return;
        int slot = _ringSlot;
        int m = _passMarks;
        _ring.PassKind[slot][m - 1] = _passKind;
        _ring.PassW[slot][m - 1] = _passW;
        _ring.PassH[slot][m - 1] = _passH;
        uint b = PassTsPerSlot * (uint)slot;
        _cmdList->EndQuery(_passQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, b + (uint)m);
        _cmdList->ResolveQueryData(_passQueryHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, b, (uint)m + 1u,
            _passTsReadback, (ulong)b * sizeof(ulong));
        Rec(RecordedOp.ResolveQuery, b, (uint)m + 1u, aux: 1);   // aux=1: the pass-timeline bank
        _ring.PassCount[slot] = m;   // intervals
        _ring.PassDropped[slot] = _passDropped;
        _ring.PassBackBufferTransitions[slot] = _frameBackBufferTransitions;
        _ring.PassPending[slot] = true;
        _ring.PassOwner[slot] = sc;
        _passOn = false;
    }

    /// <summary>Read the pass timeline this ring slot resolved on its PREVIOUS use (the ring's fence wait in
    /// SubmitDrawList proved it retired) and publish it to the target that recorded it.</summary>
    private void CollectGpuPassTimeline(int slot)
    {
        if (!_ring.PassPending[slot]) return;
        _ring.PassPending[slot] = false;
        D3D12Swapchain? owner = _ring.PassOwner[slot];
        _ring.PassOwner[slot] = null;
        if (owner is null || owner.Disposed || _passTsData == null || _passTsFreq == 0) return;
        int n = _ring.PassCount[slot];
        if (n <= 0) return;
        uint b = PassTsPerSlot * (uint)slot;
        double toMs = 1000.0 / _passTsFreq;
        GpuPassKind[] kinds = _ring.PassKind[slot];
        int[] ws = _ring.PassW[slot], hs = _ring.PassH[slot];
        for (int i = 0; i < n; i++)
        {
            ulong t0 = _passTsData[b + (uint)i], t1 = _passTsData[b + (uint)i + 1u];
            float ms = t1 > t0 ? (float)((t1 - t0) * toMs) : 0f;
            _passScratch[i] = new GpuPassTiming(kinds[i], ws[i], hs[i], ms);
        }
        ulong first = _passTsData[b], last = _passTsData[b + (uint)n];
        float whole = last > first ? (float)((last - first) * toMs) : 0f;
        owner.PublishGpuPassTimeline(_passScratch.AsSpan(0, n), whole, _ring.PassDropped[slot], _ring.PassBackBufferTransitions[slot]);
    }

    // Lazily create the always-on two-query heap + readback buffer on the first submit (the queue exists by then).
    // Sized to SubmissionRing.Depth, not FRAME_COUNT (Phase 1 §3.3/§10 Q2 — the ring is one submission deeper than the
    // swapchain's own back-buffer count).
    private void EnsureGpuExecutionTiming()
    {
        if (_gpuExecutionTimingInitTried) return;
        _gpuExecutionTimingInitTried = true;
        ulong freq;
        if (_queue == null || _queue->GetTimestampFrequency(&freq) < 0 || freq == 0) return;
        _gpuExecutionTsFreq = freq;

        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = GpuExecutionTsPerFrame * SubmissionRing.Depth;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        _gpuExecutionQueryHeap = heap;

        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = GpuExecutionTsPerFrame * SubmissionRing.Depth * sizeof(ulong);
        rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null,
            __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        {
            _gpuExecutionQueryHeap->Release(); _gpuExecutionQueryHeap = null; return;
        }
        _gpuExecutionTsReadback = readback;
        void* mapped;
        if (readback->Map(0, null, &mapped) >= 0)
        {
            _gpuExecutionTsData = (ulong*)mapped;
            D3D12MemoryDiagnostics.Track(readback, "GpuExecution.TimestampReadback", rd.Width);
        }
        else
        {
            _gpuExecutionTsReadback->Release(); _gpuExecutionTsReadback = null;
            _gpuExecutionQueryHeap->Release(); _gpuExecutionQueryHeap = null;
        }
    }

    // The ring's own fence wait (in SubmitDrawList) already retired this QUERY BANK. The bank may have been recorded
    // for a DIFFERENT swapchain than the one whose current submit caused its reuse, so publish through the owner
    // captured at resolve time — never through a device-global "last" slot. A failed/invalid resolve advances no
    // target sequence. Keyed by RING SLOT now, not back-buffer index (Phase 1 §3.3).
    private void CollectGpuExecutionTime(int slot)
    {
        if (_gpuExecutionTsData == null || _gpuExecutionTsFreq == 0 || !_ring.ExecTsPending[slot]) return;
        _ring.ExecTsPending[slot] = false;
        D3D12Swapchain? owner = _ring.Owner[slot];
        ulong ownerSubmit = _ring.OwnerSubmit[slot];
        _ring.Owner[slot] = null;
        _ring.OwnerSubmit[slot] = 0;
        if (owner is null || owner.Disposed) return;
        uint q = GpuExecutionTsPerFrame * (uint)slot;
        ulong begin = _gpuExecutionTsData[q], end = _gpuExecutionTsData[q + 1u];
        if (end <= begin) return;
        double ms = (end - begin) * 1000.0 / _gpuExecutionTsFreq;
        if (!double.IsFinite(ms) || ms <= 0.0) return;
        owner.PublishGpuRenderSample(ms, ownerSubmit, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void ReleaseGpuTimingResources()
    {
        if (_gpuExecutionTsReadback != null)
        {
            if (_gpuExecutionTsData != null) _gpuExecutionTsReadback->Unmap(0, null);
            D3D12MemoryDiagnostics.Release(_gpuExecutionTsReadback, "GpuExecution.TimestampReadback");
            _gpuExecutionTsReadback->Release();
            _gpuExecutionTsReadback = null; _gpuExecutionTsData = null;
        }
        if (_gpuExecutionQueryHeap != null) { _gpuExecutionQueryHeap->Release(); _gpuExecutionQueryHeap = null; }
        if (_passTsReadback != null)
        {
            if (_passTsData != null) _passTsReadback->Unmap(0, null);
            D3D12MemoryDiagnostics.Release(_passTsReadback, "GpuPassTimeline.TimestampReadback");
            _passTsReadback->Release(); _passTsReadback = null; _passTsData = null;
        }
        if (_passQueryHeap != null) { _passQueryHeap->Release(); _passQueryHeap = null; }

        // Ring-keyed banks (Phase 1 §3.3). The execution timer's Owner/OwnerSubmit and the pass timeline's PassOwner are
        // separate arrays; clear both sets of retired state.
        Array.Clear(_ring.ExecTsPending, 0, _ring.ExecTsPending.Length);
        Array.Clear(_ring.Owner, 0, _ring.Owner.Length);
        Array.Clear(_ring.OwnerSubmit, 0, _ring.OwnerSubmit.Length);
        Array.Clear(_ring.PassPending, 0, _ring.PassPending.Length);
        Array.Clear(_ring.PassOwner, 0, _ring.PassOwner.Length);
        _gpuExecutionTsFreq = 0; _passTsFreq = 0;
        _gpuExecutionTimingInitTried = false; _passInitTried = false;
        _passOn = false;
        for (int i = 0; i < _swapchains.Count; i++)
        {
            _swapchains[i].InvalidateGpuRenderSample();
            _swapchains[i].InvalidateGpuPassTimeline();
            _swapchains[i].InvalidateFrameCounters();
            _swapchains[i].InvalidateRectSubmittedArea();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Also true while <see cref="DrainImageJobs"/> holds a job it carried over its per-turn budget: the host's
    /// wake predicate (<c>WakeReasons.ImagesPending</c>) and its skip-submit gate both key off this, so the carried job
    /// gets its next drain turn even when nothing else is dirty — without it a budget-deferred cover could sit until
    /// the next unrelated frame.</remarks>
    public bool HasPendingUploads => _hasHeldImageJob || (_imageTextures?.HasPendingUploads ?? false);

    /// <summary>Fence-only maintenance for an elided/skip-submit frame: reclaims image resources whose retire fence
    /// has completed without opening a command list or presenting. Same thread confinement as the rest of the image
    /// texture store (<see cref="ImageTextureStore.ReclaimCompleted"/> asserts it internally): a no-op assert in
    /// default/force-sync (any thread may call it — matches <see cref="DrainImageJobs"/> and UI-staged Stage/Free
    /// before the async seam arms), but RENDER-THREAD ONLY once <see cref="MarkImageUploadsRenderConfined"/> has
    /// armed the store (a stray call off the render thread then throws under FGGUARD, erased in Release). Mirrors
    /// the fence read <see cref="DrainImageJobs"/> already uses.</summary>
    public void ReclaimCompletedUploads()
    {
        if (_imageTextures is null || _fence == null) return;
        _imageTextures.ReclaimCompleted(_fence->GetCompletedValue());
    }

    /// <inheritdoc/>
    // Phase 1 (§3.3): per-target now. These IGpuDevice members are kept (forwarding to the primary) so callers that
    // have not yet moved to `_swapchain.X` (agent 1-D, AppHost.cs) keep compiling; ISwapchain gains the same members
    // (D3D12Swapchain below) as the seam's real, per-target home — a detached child's suppression must never reach
    // through to the primary, or vice versa.
    public bool TextRepaintPending => _primarySwapchain!.Frame.TextRepaintPending;

    public void SuppressLatencyWaitOnce() => _primarySwapchain!.Frame.SkipLatencyOnce = true;

    public void SuppressVsyncOnce() => _primarySwapchain!.Frame.SkipVsyncOnce = true;

    public void HintSettlePresent() => _primarySwapchain!.Frame.HintSettlePresent = true;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    private bool CheckTearingSupport()
    {
        IDXGIFactory5* f5;
        if ((int)_factory->QueryInterface(__uuidof<IDXGIFactory5>(), (void**)&f5) < 0) return false;
        int allow = 0;
        HRESULT hr = f5->CheckFeatureSupport(DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow, sizeof(int));
        f5->Release();
        return (int)hr >= 0 && allow != 0;
    }

    // The device's ONLY out-of-frame fence writer besides SignalFrame. _fenceValue and the auto-reset _fenceEvent have
    // exactly one toucher at a time because every caller is render-OWNED (INCIDENT 2026-09 §1.3: the detached child's
    // unparked DisposeSwapchain used to run this on the UI thread against the render thread's SignalFrame/WaitForFrame
    // — two ++_fenceValue writers, and one thread could consume the other's wake of the shared event).
    internal void WaitForGpu()
    {
        AssertDeviceOwner();
        ulong v = ++_fenceValue;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.Signal(_queue, _fence, v), "queue.Signal");   // GEN-COM (wired)
        ulong completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (completed == ulong.MaxValue)
        {
            NoteRemovedFenceValue();
            throw new InvalidOperationException("GPU fence reported device removal.");
        }
        if (completed < v)
        {
            Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.SetEventOnCompletion(_fence, v, (void*)_fenceEvent), "SetEventOnCompletion");   // GEN-COM (wired)
            bool retired;
            if (_signalDeviceLostInsteadOfThrow) retired = WaitFenceEventBounded(v, forceResetOnStall: false);   // Step 4: no INFINITE hang on a lost device (async); readback/flush never gates a present → soft-warn only, no forced reset
            else
            {
                uint wait = WaitForSingleObject(_fenceEvent, INFINITE);
                if (wait == 0xFFFFFFFFu) throw new InvalidOperationException("WaitForSingleObject(GPU fence) failed.");
                completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
                if (completed == ulong.MaxValue) NoteRemovedFenceValue();
                retired = completed != ulong.MaxValue && completed >= v;
            }
            if (!retired) throw new InvalidOperationException("GPU fence did not reach the awaited value.");
        }
    }

    // The [d3d12.scratch] evidence edge (docs/plans/evidence-diagnostics-implementation.md §A.3): one always-on line when
    // a composite turn REFUSES scratch leases after one that refused none — never per frame while it persists.
    private bool _scratchRefusedLogged;

    private void NoteScratchRefusedEdge(int refused)
    {
        if (refused <= 0) { _scratchRefusedLogged = false; return; }
        if (_scratchRefusedLogged) return;
        _scratchRefusedLogged = true;
        FluentGpu.Foundation.Diag.Line("[d3d12.scratch] refused=" + refused.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " cap=" + SurfacePool.ScratchCap.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " leases=" + (_surfaces?.ScratchLeases ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (every scratch slot was leased: an inline group / blur / degraded chunk drew nothing this turn)");
    }

    /// <summary>The evidence bundle's frame capture (<see cref="IGpuDevice.TryCaptureBackBuffer"/>): the just-presented back
    /// buffer through <see cref="CaptureBgra"/> — the render-owner thread, right after its present.</summary>
    public bool TryCaptureBackBuffer(out byte[]? bgra, out int width, out int height)
    {
        if (_primarySwapchain is null) { bgra = null; width = height = 0; return false; }
        bgra = CaptureBgra(out width, out height);
        return true;
    }

    /// <summary>
    /// Debug-only: read the last-rendered back buffer back to CPU as tightly-packed, top-down BGRA8. Used by the
    /// <c>--screenshot</c> tooling to produce a PNG for visual fidelity diffing — NOT a hot-path method (it stalls
    /// the GPU). With FLIP_DISCARD the just-presented buffer at <c>_frameIndex</c> is still intact until reused.
    /// </summary>
    public byte[] CaptureBgra(out int width, out int height)
    {
        AssertDeviceOwner();
        if (_primarySwapchain is null) throw new InvalidOperationException("CreateSwapchain must be called before CaptureBgra.");
        BeginTargetFrame(_primarySwapchain.Frame);
        WaitForGpu();   // ensure the last frame finished rendering before we copy it
        width = (int)_w; height = (int)_h;
        // f.FrameIndex is whatever the last successful SubmitDrawList left it at — NOT re-queried via
        // GetCurrentBackBufferIndex here, which (post-Present) would already point at the NEXT, not-yet-rendered
        // buffer. FLIP_DISCARD keeps the just-presented buffer's contents intact until that next buffer is reused.
        ID3D12Resource* back = _f!.Target.BackBuffers[_f!.FrameIndex];

        // Footprint of subresource 0 (RowPitch is aligned to 256 → may exceed width*4; we re-pack on the CPU side).
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = _w; desc.Height = _h; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint numRows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &numRows, &rowBytes, &total);

        // Readback (CPU-visible) staging buffer.
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC bd = default;
        bd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
        bd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &bd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&readback), "Capture.Readback");
        D3D12MemoryDiagnostics.Track(readback, "Capture.Readback", total);   // audit gpu mem-01: was a [d3d-mem]/DiagResourceTotals blind spot

        int slot = _ring.NextSlot;
        ID3D12CommandAllocator* alloc = _ring.Allocators[slot];
        Check(alloc->Reset(), "capture.alloc.Reset");
        Check(_cmdList->Reset(alloc, null), "capture.cmd.Reset");
        Rec(RecordedOp.ListReset, (uint)slot, _w << 16 | (_h & 0xFFFF), aux: 1);   // aux=1: the capture list, not a frame
        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);

        D3D12_TEXTURE_COPY_LOCATION dst = default;
        dst.pResource = readback;
        dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.Anonymous.PlacedFootprint = fp;
        D3D12_TEXTURE_COPY_LOCATION src = default;
        src.pResource = back;
        src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.Anonymous.SubresourceIndex = 0;
        _cmdList->CopyTextureRegion(&dst, 0, 0, 0, &src, null);
        Rec(RecordedOp.CopyTexture, (uint)(nint)back, (uint)total);

        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        Check(_cmdList->Close(), "capture.cmd.Close");
        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, (void**)&execList);   // GEN-COM (wired)
        WaitForGpu();

        byte[] outp = new byte[(long)width * height * 4];
        void* mapped;
        D3D12_RANGE rr = default; rr.Begin = 0; rr.End = (nuint)total;
        Check(readback->Map(0, &rr, &mapped), "capture.Map");
        uint pitch = fp.Footprint.RowPitch;
        for (int y = 0; y < height; y++)
        {
            byte* srcRow = (byte*)mapped + (ulong)y * pitch;
            new ReadOnlySpan<byte>(srcRow, width * 4).CopyTo(outp.AsSpan(y * width * 4));
        }
        D3D12_RANGE wrote = default;   // we wrote nothing back
        readback->Unmap(0, &wrote);
        D3D12MemoryDiagnostics.Release(readback, "Capture.Readback");
        readback->Release();
        EndTargetFrame();
        return outp;
    }

    internal void Resize(D3D12Swapchain target, uint w, uint h)
    {
        AssertDeviceOwner();
        if (w < 1) w = 1; if (h < 1) h = 1;
        if (target.Disposed || (w == target.W && h == target.H)) return;
        BeginTargetFrame(target.Frame);
        // Phase 1 (§3.4, pal-rhi.md §5.4 as designed): CPU-wait only the fence values THIS target's in-flight work
        // stamped — its submits AND its last present (TargetFenceHorizon) — never a full device drain: a detached child's
        // resize must not stall the main window's recorder, and the old back buffers must not be released under a present.
        WaitForFenceValue(TargetFenceHorizon(target.Frame));
        // Swapchain-sized; recreated lazily at the new size on the next stencil scope. EXPLICIT target (INCIDENT 2026-09
        // §1.4): the old no-arg release freed only the working copy — this clears the target's own storage directly
        // (Phase 1 removes the working-copy concept structurally, so there is no reload to guard against any more).
        ReleaseStencilDsv(target.Frame);
        D3D12MemoryDiagnostics.Resize("Swapchain", w, h);
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            if (target.BackBuffers[i] != null)
            {
                D3D12MemoryDiagnostics.Release(target.BackBuffers[i], $"Swapchain.BackBuffer[{i}]");
                target.BackBuffers[i]->Release();
                target.BackBuffers[i] = null;
            }
        }
        Check(target.SwapChain->ResizeBuffers(FRAME_COUNT, w, h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, target.SwapChainFlags), "ResizeBuffers");
        target.PpEpoch++;   // new buffers hold nothing: the next frame composites and presents whole
        target.W = w; target.H = h;
        _w = w; _h = h;
        CreateRtvs(target);
        target.Backdrop?.SetBounds(w, h);   // resize the WUC backdrop/content visuals to match
        EndTargetFrame();
    }

    public Size2 SizePx => _primarySwapchain is { } sc ? sc.SizePx : new(_w, _h);

    internal void DisposeSwapchain(D3D12Swapchain target)
    {
        AssertDeviceOwner();   // a detached child's teardown runs under the parent loop's park (AppHost.Dispose, INCIDENT 2026-09 §1.3)
        if (target.Disposed) return;
        // F117: wait only for the work THIS target's own frames still have in flight (the Resize rule), never a device-wide
        // WaitForGpu: that signalled a fresh device fence and blocked until the queue reached it, i.e. until every queued frame of
        // OTHER windows (the main window's included) retired too, while the UI sat parked inside a pop-out / flyout close. What
        // is released below (this target's command list, stencil DSV, back buffers, RTV heap, DComp visuals) is only ever
        // referenced by this target's own submits, which its fence ledger covers; DComp / DXGI keep their own references for
        // anything still being composed, and the shared pools (atlases, image textures, ring allocators) are not released here.
        if (_device != null) WaitForFenceValue(TargetFenceHorizon(target.Frame));
        ReleaseSwapchainResources(target);
        // Release this swapchain's video presenter AFTER its resources (its DcompRoot is now null → DetachChild no-ops)
        // but while the shared _dcomp is still alive (so the presenter's own child-visual ComPtrs release cleanly). This
        // is the teardown path when a detached video window's swapchain is closed.
        target.VideoPresenter?.Dispose();
        target.VideoPresenter = null;
        _swapchains.Remove(target);
        if (_primarySwapchain == target) { _primarySwapchain = null; _compositeOwner = 0; }   // the next primary is a new composite owner
        // Phase 1 (§3.5): no more device-global working copy to drop — ReleaseSwapchainResources already zeroed every
        // ComPtr on `target` itself (its own Frame/BackBuffers/SwapChain/RtvHeap), and `_f` never mirrors another
        // target's state in the first place (BeginTargetFrame/EndTargetFrame bracket it to one call).
    }

    /// <summary>The fence value that retires everything a target has in flight: the highest value its own submits stamped
    /// (<see cref="TargetFrameState.LastSubmitFence"/> is the running max; the per-back-buffer ledger is folded in as well so a
    /// stale max can never under-wait) AND the value signalled right after its last Present that ran
    /// (<see cref="TargetFrameState.LastPresentFence"/>: the present's own queue work comes after the submit's stamp). Fence
    /// values are device-monotonic, so waiting for this one value also covers every earlier submit of the target. 0 = nothing in
    /// flight (<see cref="WaitForFenceValue"/> returns at once). Another target's stamps are never read: that is the whole point of
    /// F117 (a secondary's dispose must not wait on the primary's frame).</summary>
    internal static ulong TargetFenceHorizon(TargetFrameState f)
    {
        ulong v = f.LastSubmitFence;
        if (f.LastPresentFence > v) v = f.LastPresentFence;
        for (int i = 0; i < f.FenceValues.Length; i++)
            if (f.FenceValues[i] > v) v = f.FenceValues[i];
        return v;
    }

    private void ReleaseSwapchainResources(D3D12Swapchain target)
    {
        if (target.Disposed) return;
        // A query bank can still name this target after its fence retired but before another submit collected it. Drop
        // that ownership now so the bank neither retains a closed popup nor publishes into it on a later target's submit.
        // Ring-keyed now (Phase 1 §3.3) — the execution timer's Owner/OwnerSubmit and the pass timeline's PassOwner.
        for (int i = 0; i < _ring.Owner.Length; i++)
        {
            if (ReferenceEquals(_ring.PassOwner[i], target)) { _ring.PassPending[i] = false; _ring.PassOwner[i] = null; }
            if (!ReferenceEquals(_ring.Owner[i], target)) continue;
            _ring.ExecTsPending[i] = false;
            _ring.Owner[i] = null;
            _ring.OwnerSubmit[i] = 0;
        }
        target.InvalidateGpuRenderSample();
        target.InvalidateGpuPassTimeline();
        target.InvalidateFrameCounters();
        target.InvalidateRectSubmittedArea();
        // Per-target stencil DSV (CANDIDATE FIX, INCIDENT 2026-09) — this target owns it now (TargetFrameState.
        // StencilDsv*), so its teardown belongs HERE rather than the old single device-global release site. Must run
        // BEFORE target.Disposed = true below only in the sense that ReleaseStencilDsv(target.Frame) itself never
        // checks Disposed; ordering relative to the rest of this method does not matter (the DSV shares no ComPtr
        // with the swapchain/back-buffer/RTV-heap resources released below).
        ReleaseStencilDsv(target.Frame);
        // Phase 1 (§3.1/§3.3): the per-target command list and layer pools are torn down here too — they used to be
        // device-global (freed once, in Dispose/RecoverDevice) and are now this target's own.
        if (target.Frame.List != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(target.Frame.List); target.Frame.List = null; target.Frame.List4 = null; }
        target.Disposed = true;
        // Tear down the WUC backdrop FIRST (it holds a composition surface wrapping the swapchain).
        target.Backdrop?.Dispose();
        target.Backdrop = null;
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            if (target.BackBuffers[i] != null)
            {
                D3D12MemoryDiagnostics.Release(target.BackBuffers[i], $"Swapchain.BackBuffer[{i}]");
                target.BackBuffers[i]->Release();
                target.BackBuffers[i] = null;
            }
        }
        if (target.DcompVisual != null) { target.DcompVisual->Release(); target.DcompVisual = null; }
        if (target.DcompRoot != null) { target.DcompRoot->Release(); target.DcompRoot = null; }
        if (target.DcompTarget != null) { target.DcompTarget->Release(); target.DcompTarget = null; }
        if (target.SwapChain != null) { target.SwapChain->Release(); target.SwapChain = null; }
        // The latency waitable belonged to the swapchain just released: the handle is void and so is any credit taken
        // against it (InitSwapChain re-fetches both on a recovery / recreate).
        target.FrameLatencyWaitable = HANDLE.NULL;
        target.HasLatencyWaitable = false;
        target.LatencyCreditHeld = false;
        if (target.RtvHeap != null)
        {
            D3D12MemoryDiagnostics.Release(target.RtvHeap, "Swapchain.RtvHeap");
            target.RtvHeap->Release();
            target.RtvHeap = null;
        }
    }

    // Step 4 (async): rebuild the lost device. Render-confined (this is the SOLE ComPtr owner; the UI is parked/blocking).
    // Mirrors Dispose's teardown MINUS the leading WaitForGpu (the dead fence never completes — canon §9.2) and MINUS the
    // swapchain-object removal (we recreate them in place), then re-runs the ctor's init sequence. All GPU state is
    // CPU-reconstructible: PSOs recompile from embedded HLSL, the glyph atlas re-rasterizes on demand, and resident images
    // re-decode via ImageCache.ReRealizeAllResident (the UI calls it on the recover-done frame).
    public void RecoverDevice()
    {
        AssertSubmitThread();
        // 1. Release every swapchain's GPU resources but KEEP the D3D12Swapchain objects (their W/H/Hwnd/Composited/etc.
        //    survive for re-init); reset Disposed since we are recreating, not tearing down. ReleaseSwapchainResources
        //    now also tears down each target's OWN command list + layer pools (Phase 1 §3.1/§3.3 — used to be device-
        //    global, released once below instead).
        for (int i = 0; i < _swapchains.Count; i++)
        {
            ReleaseSwapchainResources(_swapchains[i]);
            // The video presenter's child visuals / wrapped surfaces were created by the DComp device released in step 2
            // and cannot be parented under the new device's root: dispose it now (its DcompRoot is already null, so it only
            // frees its own ComPtrs, while _dcomp is still alive) and let GetVideoPresenter build a fresh one on the new
            // device. VideoSurfaceRegistry.Drain sees the new instance and re-creates, re-binds and re-places every live surface.
            _swapchains[i].VideoPresenter?.Dispose();
            _swapchains[i].VideoPresenter = null;
            _swapchains[i].Disposed = false;
        }
        // The presenters' backing visuals are gone with them; the shared black backing content was built on the queue
        // released below, so drop it too — the next video slot recreates it (EnsureVideoBacking) on the new device.
        ReleaseVideoBacking();
        // 2. Release device-level ComPtrs + pipelines (null the pipe fields so EnsurePipelines re-runs). NO WaitForGpu.
        ReleaseGpuTimingResources();
        // Stencil DSVs are per-target now (CANDIDATE FIX, INCIDENT 2026-09) — step 1's ReleaseSwapchainResources loop
        // above already released every swapchain's own StencilDsv/StencilDsvHeap; there is nothing device-global left
        // to release here (the old single call site this replaced).
        if (_infoQueue != null) { _infoQueue->Release(); _infoQueue = null; }
        if (_dcomp != null) { _dcomp->Release(); _dcomp = null; }
        _videoCommitDue = false;   // the edits it described went with the old composition device
        _glyphs?.Dispose(); _glyphs = null;
        _imagePipe?.Dispose(); _imagePipe = null;
        _bakedBlur?.Dispose(); _bakedBlur = null;
        // The retained-tile surfaces died with the device: the host's target-epoch bump invalidates every tile, and the
        // next composite recreates what it rasters.
        _surfaces?.Dispose(); _surfaces = null;
        _feedbackTrails.Clear(); Volatile.Write(ref _feedbackLive, 0);   // F6: trails restart empty after a device loss
        _compositor?.Dispose(); _compositor = null;
        _imageTextures?.Dispose(); _imageTextures = null;
        _shadowPipe?.Dispose(); _shadowPipe = null;
        _arcPipe?.Dispose(); _arcPipe = null;
        _polylinePipe?.Dispose(); _polylinePipe = null;
        _gradPipe?.Dispose(); _gradPipe = null;
        _pathPipe?.Dispose(); _pathPipe = null;
        _seriesPipe?.Dispose(); _seriesPipe = null;
        _spritePipe?.Dispose(); _spritePipe = null;
        _rectPipe?.Dispose(); _rectPipe = null;
        // AFTER every pipeline that borrows it (they hold no COM of their own for instance data any more, but the
        // ordering keeps "release the borrower, then the owner" true by construction).
        _uploadArena?.Dispose(); _uploadArena = null;
        _sdf?.Dispose(); _sdf = null;
        // No device-global _cmdList to release (Phase 1 §3.1 — each target's own list was released above); the ring's
        // allocators are, though (they are device-shared CPU-written banks).
        for (int i = 0; i < SubmissionRing.Depth; i++)
            if (_ring.Allocators[i] != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_ring.Allocators[i]); _ring.Allocators[i] = null; }
        if (_fence != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_fence); _fence = null; }
        if (_queue != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_queue); _queue = null; }
        ReleaseAdapter3();
        if (_factory != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_factory); _factory = null; }
        if (_device != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_device); _device = null; }
        if (_fenceEvent != HANDLE.NULL) { CloseHandle(_fenceEvent); _fenceEvent = HANDLE.NULL; }

        // 3. Recreate the device/queue/allocator-ring/fence/event (InitDevice resets _fenceValue = 0).
        InitDevice();
        // 4. Zero every target's back-buffer-keyed fence ledger AND the ring's slot-keyed one, so WaitForFenceValue's
        //    v==0 early-out fires for the first submit on every stale pre-loss target/slot — the fresh fence never
        //    reaches values stamped by the device that was just replaced.
        for (int i = 0; i < _swapchains.Count; i++)
        {
            System.Array.Clear(_swapchains[i].Frame.FenceValues, 0, _swapchains[i].Frame.FenceValues.Length);
            _swapchains[i].Frame.LastSubmitFence = 0;
            _swapchains[i].Frame.LastPresentFence = 0;
        }
        System.Array.Clear(_ring.Fence, 0, _ring.Fence.Length);
        // 5. Recreate all pipelines + a fresh (empty) image store; re-arm async image confinement on the new store.
        EnsurePipelines();
        if (_signalDeviceLostInsteadOfThrow) _imageTextures?.MarkRenderConfined();
        // 6. Recreate every swapchain (SwapChain / RtvHeap / BackBuffers / Backdrop / its own command list) at its
        //    retained size, then rebind its DirectComposition graph HERE (RecoverDevice is render-confined,
        //    AssertSubmitThread at the top) so the recover frame is composited-correct on the render thread without
        //    waiting for the next Present's lazy bind.
        for (int i = 0; i < _swapchains.Count; i++)
        {
            InitSwapChain(_swapchains[i]);
            if (_swapchains[i].Composited) BindDComp(_swapchains[i]);
        }
        // No Activate(_primarySwapchain) — Phase 1 has no device-global working copy to prime; the next
        // SubmitDrawList/Present calls BeginTargetFrame for whichever target it services.
        // 7. Healthy again.
        System.Threading.Volatile.Write(ref _deviceLostReason, 0);
    }

    public void Dispose()
    {
        AssertDeviceOwner();   // after the render loop was joined (RenderThread.Dispose adopts ownership for the UI)
        if (_device != null) WaitForGpu();
        for (int i = _swapchains.Count - 1; i >= 0; i--)
        {
            // Video spine: release each swapchain's presenter (child visuals + wrapped surface content) BEFORE the shared
            // _dcomp below. ReleaseSwapchainResources has already nulled this swapchain's DcompRoot, so the presenter's
            // DetachChild no-ops (guarded) — it only frees its own child ComPtrs.
            ReleaseSwapchainResources(_swapchains[i]);
            _swapchains[i].VideoPresenter?.Dispose();
            _swapchains[i].VideoPresenter = null;
        }
        _swapchains.Clear();
        _primarySwapchain = null;
        foreach (nint sc in _testSurfaceSwapchains)
            if (sc != 0) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release((void*)sc);
        _testSurfaceSwapchains.Clear();
        ReleaseVideoBacking();   // after every presenter (their backing visuals share this content), before the DComp device
        if (_dcomp != null) _dcomp->Release();
        _glyphs?.Dispose();
        _imagePipe?.Dispose();
        _bakedBlur?.Dispose();
        _surfaces?.Dispose();
        ReleaseDamageChecks();
        _compositor?.Dispose();
        _imageTextures?.Dispose();
        _shadowPipe?.Dispose();
        _arcPipe?.Dispose();
        _polylinePipe?.Dispose();
        _gradPipe?.Dispose();
        _pathPipe?.Dispose();
        _seriesPipe?.Dispose();
        _spritePipe?.Dispose();
        _rectPipe?.Dispose();
        _uploadArena?.Dispose();   // after every borrower (see the device-lost teardown's note)
        _sdf?.Dispose();
        // No device-global _backBuffers to null (Phase 1 §3.1 — each target's own BackBuffers[] was released above).
        ReleaseGpuTimingResources();
        // Stencil DSVs are per-target now (CANDIDATE FIX, INCIDENT 2026-09) — the per-swapchain
        // ReleaseSwapchainResources loop above already released every swapchain's own StencilDsv/StencilDsvHeap;
        // there is nothing device-global left to release here (the old single call site this replaced).
        if (_infoQueue != null) { _infoQueue->Release(); _infoQueue = null; }
        D3D12MemoryDiagnostics.Snapshot("D3D12Device.Dispose");
        // No device-global _cmdList to release (Phase 1 §3.1 — each target's own list was released above by
        // ReleaseSwapchainResources); the ring's allocators are, though (they are device-shared CPU-written banks).
        for (int i = 0; i < SubmissionRing.Depth; i++)
            if (_ring.Allocators[i] != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_ring.Allocators[i]);
        if (_fence != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_fence);
        if (_queue != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_queue);
        ReleaseAdapter3();
        if (_factory != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_factory);
        if (_device != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_device);
        if (_fenceEvent != HANDLE.NULL) CloseHandle(_fenceEvent);
        if (_slotGraceTimer != HANDLE.NULL) { CloseHandle(_slotGraceTimer); _slotGraceTimer = HANDLE.NULL; }
    }
}

public sealed unsafe class D3D12Swapchain : ISwapchain
{
    internal readonly D3D12Device Device;
    internal readonly HWND Hwnd;
    internal readonly bool Composited;
    internal IDXGISwapChain3* SwapChain;
    internal ID3D12DescriptorHeap* RtvHeap;
    internal readonly ID3D12Resource*[] BackBuffers = new ID3D12Resource*[(int)D3D12Device.FRAME_COUNT];
    internal HANDLE FrameLatencyWaitable;
    internal bool HasLatencyWaitable;
    // The frame-latency waitable is a SEMAPHORE, not a level — one successful wait reserves ONE present slot and only a
    // Present gives it back. True ⇒ a wait (D3D12Device.TryTakePresentSlot for the primary, SubmitDrawList's own
    // WaitForLatency for any target) has already taken this swapchain's slot for the frame
    // now in production, so SubmitDrawList must NOT wait again (that would block a whole extra present cycle) and the
    // next Present that actually runs clears it. Render-thread-only (submit/present are render-confined), so a plain
    // field is the whole synchronization story. Reset to false wherever the waitable handle is (re)created or released.
    internal bool LatencyCreditHeld;
    // Rate limiter for the non-primary latency-wait log (D3D12Device.NoteNonPrimaryLatencyWait): QPC of the last line, and
    // how many qualifying waits it swallowed since. Render-thread-only, like LatencyCreditHeld.
    internal long LatencyWaitLogQpc;
    internal int LatencyWaitsSuppressed;
    // This swapchain's DXGI maximum frame latency when it is NOT the primary (D3D12Device.SetPresentQueueDepth(target, depth) —
    // a detached pop-out's own depth policy); the primary's lives on the device (_presentQueueDepth). Render thread writes
    // (Volatile), any thread may read. Starts at the creation depth, so a rebuild of the swapchain restores it.
    internal int PresentQueueDepth = (int)D3D12Device.InitialPresentQueueDepth;
    internal uint SwapChainFlags;
    internal bool TearingSupported;
    // partial present (D3D12Device.PartialPresent.cs): FLIP_SEQUENTIAL, the buffer epoch (bumped by every create / resize),
    // and the dirty rects the next Present carries (0 = whole frame); a stood-down present makes the next one whole.
    internal bool SequentialFlip;
    internal uint PpEpoch;
    internal readonly RECT[] PpPresentRects = new RECT[RepaintDamageRegion.MaxRects];
    internal int PpPresentCount;
    internal bool PpNeedFullPresent;
    internal uint W, H;
    // FrameIndex, StencilDsv/StencilDsvHeap/StencilDsvW/StencilDsvH DELETED (Phase 1, detached-window-render-
    // isolation-implementation.md §3.1/§3.5): they were the Activate/StoreActive per-target mirror of state that now
    // lives ONLY on Frame (below) — never copied into (or back out of) a device working field, so a detached child's
    // create/dispose/resize can no longer clobber another target's in-flight recording (INCIDENT 2026-09 §1.2/§1.3/§1.4).
    internal IDCompositionTarget* DcompTarget;
    internal IDCompositionVisual* DcompRoot;     // video spine (M0): the tree root — UI child z-above, video children z-below
    internal IDCompositionVisual* DcompVisual;   // the UI child (owns the swapchain content); topmost, painted every frame
    internal bool DcompBindPending;   // seam: DComp graph deferred out of UI-thread InitSwapChain; bound on the presenting thread (see BindDComp)
    internal FluentGpu.Pal.Windows.DCompVideoPresenter? VideoPresenter;   // per-window video presenter bound to THIS swapchain's DComp root (lazily created; disposed with the swapchain)
    internal CompositionBackdrop? Backdrop;   // non-null ⇒ WUC desktop-acrylic popup (replaces the DComp path)
    // This target's per-submit recording + damage/present state (Phase 1 §3.1). Created once, here, never rebuilt —
    // BeginTargetFrame/EndTargetFrame take a reference to it for the duration of one submit; nothing ever copies it
    // into (or out of) a device field.
    internal readonly TargetFrameState Frame;
    internal readonly bool DesktopAcrylic;
    internal readonly ColorF AcrylicTint;
    internal readonly float CornerRadiusPx;
    // Creation ordinal (1, 2, … saturating at 255; 0 = unknown): the target id the always-on forensic ring tags every
    // recorded op with, so a [d3d12.forensic] line names which window a stray op was recorded for (INCIDENT 2026-09).
    internal readonly byte Ordinal;
    internal bool Disposed;
    // ── Present-topology attribution (WS-B) ── cached per swapchain so [d3d12.present] emits on CHANGE only.
    internal HMONITOR TopologyMonitor;     // monitor at last check (NULL = never checked)
    internal int PresentTopologyState;     // D3D12Device.Topology* — 0 unknown / 1 owned / 2 cross-adapter / 3 no-outputs
    internal FluentGpu.Media.VideoOverlayCaps OverlayCaps;   // F249: the owning output's overlay-plane verdict at the last topology check (default = unprobed)
    // Per-window refresh period (mixed-refresh-rate audit): resolved on the SAME edge as TopologyMonitor above
    // (D3D12Device.SamplePresentTopology), via DisplayInfo.ForWindow — 0 until first resolved, meaning "use the
    // DWM-global fallback" (SamplePresentStats only overrides its local `refreshPeriod` when this is > 0).
    internal long CachedRefreshPeriodQpc;
    // Whole-frame execution samples are TARGET state, not device state. Main + popup/child hosts share one command queue;
    // publishing here prevents a heavy popup submit from steering the main host's adaptive governor (or vice versa).
    private long _gpuSampleVersion;        // seqlock: odd while any field below changes
    private long _gpuSubmitSequence;
    private long _gpuSampleSequence;
    private long _gpuSampleSubmitSequence;
    private long _gpuSamplePublishedQpc;
    private double _gpuSampleExecutionMs;
    // Pass-granular GPU timeline of this target's most recently RETIRED instrumented frame (seqlock: odd version =
    // write in flight). Written by the render thread one submission after the frame, read by any thread.
    private long _passVersion, _passSequence;
    private int _passValid, _passCount, _passDropped, _passBackBufferTransitions;
    private float _passWholeMs;
    private readonly GpuPassTiming[] _passes = new GpuPassTiming[GpuPassTimeline.MaxPasses];
    // Always-on device counters of this target's most recent successful submit (same seqlock shape).
    private long _countersVersion;
    private int _countersValid;
    private GpuFrameCounters _counters;
    private const int RectSubmittedAreaTopCapacity = 8;
    private long _rectAreaVersion;
    private long _rectAreaSequence;
    private int _rectAreaValid, _rectAreaHasArea, _rectAreaOpaqueInstances, _rectAreaBlendedInstances, _rectAreaTopCount;
    private double _rectAreaOpaquePx2, _rectAreaBlendedPx2;
    private readonly RectSubmittedAreaItem[] _rectAreaTop = new RectSubmittedAreaItem[RectSubmittedAreaTopCapacity];

    internal D3D12Swapchain(D3D12Device device, HWND hwnd, uint w, uint h, bool composited, bool desktopAcrylic, ColorF acrylicTint, float cornerRadiusPx,
        byte ordinal = 0)
    {
        Device = device;
        Ordinal = ordinal;
        Hwnd = hwnd;
        W = w;
        H = h;
        Composited = composited;
        DesktopAcrylic = desktopAcrylic;
        AcrylicTint = acrylicTint;
        CornerRadiusPx = cornerRadiusPx;
        Frame = new TargetFrameState(this);
    }

    public Size2 SizePx => new(W, H);

    private int _presentedContent;
    /// <inheritdoc/>
    /// <remarks>Written by the render thread (the sole presenter) at the end of a successful
    /// <see cref="D3D12Device.Present"/>; read by the UI thread's popup reveal gate + wake computation. A one-way
    /// latch, so the volatile int needs no further ordering.</remarks>
    public bool HasPresentedContent => System.Threading.Volatile.Read(ref _presentedContent) != 0;
    internal void NotePresentedContent()
    {
        if (System.Threading.Volatile.Read(ref _presentedContent) == 0)
            System.Threading.Volatile.Write(ref _presentedContent, 1);
    }

    internal ulong NoteGpuSubmit()
    {
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
        long submit = System.Threading.Interlocked.Increment(ref _gpuSubmitSequence);
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
        return unchecked((ulong)submit);
    }

    internal void PublishGpuRenderSample(double executionMs, ulong submitSequence, long publishedQpc)
    {
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
        System.Threading.Volatile.Write(ref _gpuSampleExecutionMs, executionMs);
        System.Threading.Interlocked.Exchange(ref _gpuSampleSubmitSequence, unchecked((long)submitSequence));
        System.Threading.Interlocked.Exchange(ref _gpuSamplePublishedQpc, publishedQpc);
        System.Threading.Interlocked.Increment(ref _gpuSampleSequence);
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
    }

    internal void InvalidateGpuRenderSample()
    {
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
        System.Threading.Volatile.Write(ref _gpuSampleExecutionMs, 0.0);
        System.Threading.Interlocked.Exchange(ref _gpuSampleSubmitSequence, 0);
        System.Threading.Interlocked.Exchange(ref _gpuSamplePublishedQpc, 0);
        System.Threading.Interlocked.Increment(ref _gpuSampleVersion);
    }

    public bool TryGetGpuRenderSample(out GpuRenderSample sample)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = System.Threading.Volatile.Read(ref _gpuSampleVersion);
            if ((before & 1L) != 0) continue;
            double ms = System.Threading.Volatile.Read(ref _gpuSampleExecutionMs);
            long sequence = System.Threading.Interlocked.Read(ref _gpuSampleSequence);
            long sampleSubmit = System.Threading.Interlocked.Read(ref _gpuSampleSubmitSequence);
            long publishedQpc = System.Threading.Interlocked.Read(ref _gpuSamplePublishedQpc);
            long currentSubmit = System.Threading.Interlocked.Read(ref _gpuSubmitSequence);
            long after = System.Threading.Volatile.Read(ref _gpuSampleVersion);
            if (before != after || (after & 1L) != 0) continue;
            if (sequence == 0 || sampleSubmit <= 0 || publishedQpc <= 0 || !double.IsFinite(ms) || ms <= 0.0) break;
            ulong submitAge = currentSubmit >= sampleSubmit
                ? unchecked((ulong)(currentSubmit - sampleSubmit))
                : ulong.MaxValue;
            sample = new GpuRenderSample(ms, unchecked((ulong)sequence), submitAge, publishedQpc);
            return true;
        }
        sample = default;
        return false;
    }

    internal void PublishGpuPassTimeline(ReadOnlySpan<GpuPassTiming> passes, float wholeMs, int dropped, int backBufferTransitions)
    {
        int count = Math.Min(passes.Length, _passes.Length);
        System.Threading.Interlocked.Increment(ref _passVersion);
        for (int i = 0; i < count; i++) _passes[i] = passes[i];
        _passCount = count;
        _passDropped = dropped;
        _passBackBufferTransitions = backBufferTransitions;
        _passWholeMs = wholeMs;
        _passSequence++;
        _passValid = 1;
        System.Threading.Interlocked.Increment(ref _passVersion);
    }

    internal void InvalidateGpuPassTimeline()
    {
        System.Threading.Interlocked.Increment(ref _passVersion);
        _passValid = 0;
        System.Threading.Interlocked.Increment(ref _passVersion);
    }

    /// <inheritdoc/>
    public int CopyGpuPassTimeline(Span<GpuPassTiming> dst, out GpuPassFrameSummary summary)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = System.Threading.Volatile.Read(ref _passVersion);
            if ((before & 1L) != 0) continue;
            int valid = _passValid, count = _passCount, dropped = _passDropped, bbt = _passBackBufferTransitions;
            float whole = _passWholeMs;
            long sequence = _passSequence;
            int copy = Math.Min(count, dst.Length);
            for (int i = 0; i < copy; i++) dst[i] = _passes[i];
            System.Threading.Interlocked.MemoryBarrier();
            long after = System.Threading.Volatile.Read(ref _passVersion);
            if (before != after) continue;
            if (valid == 0 || sequence == 0) break;
            summary = new GpuPassFrameSummary(unchecked((ulong)sequence), whole, count, dropped, bbt);
            return copy;
        }
        summary = default;
        return 0;
    }

    internal void PublishFrameCounters(in GpuFrameCounters counters)
    {
        System.Threading.Interlocked.Increment(ref _countersVersion);
        _counters = counters;
        _countersValid = 1;
        System.Threading.Interlocked.Increment(ref _countersVersion);
    }

    internal void InvalidateFrameCounters()
    {
        System.Threading.Interlocked.Increment(ref _countersVersion);
        _countersValid = 0;
        System.Threading.Interlocked.Increment(ref _countersVersion);
    }

    /// <inheritdoc/>
    public bool TryGetFrameCounters(out GpuFrameCounters counters)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = System.Threading.Volatile.Read(ref _countersVersion);
            if ((before & 1L) != 0) continue;
            int valid = _countersValid;
            GpuFrameCounters copy = _counters;
            System.Threading.Interlocked.MemoryBarrier();
            long after = System.Threading.Volatile.Read(ref _countersVersion);
            if (before != after) continue;
            if (valid == 0) break;
            counters = copy;
            return true;
        }
        counters = default;
        return false;
    }

    internal void PublishRectSubmittedArea(int opaqueInstances, int blendedInstances, bool hasArea,
        double opaquePx2, double blendedPx2, ReadOnlySpan<RectSubmittedAreaItem> blendedTop)
    {
        System.Threading.Interlocked.Increment(ref _rectAreaVersion);
        System.Threading.Volatile.Write(ref _rectAreaOpaqueInstances, opaqueInstances);
        System.Threading.Volatile.Write(ref _rectAreaBlendedInstances, blendedInstances);
        System.Threading.Volatile.Write(ref _rectAreaHasArea, hasArea ? 1 : 0);
        System.Threading.Volatile.Write(ref _rectAreaOpaquePx2, opaquePx2);
        System.Threading.Volatile.Write(ref _rectAreaBlendedPx2, blendedPx2);
        int count = Math.Min(blendedTop.Length, RectSubmittedAreaTopCapacity);
        for (int i = 0; i < count; i++) _rectAreaTop[i] = blendedTop[i];
        System.Threading.Volatile.Write(ref _rectAreaTopCount, count);
        System.Threading.Interlocked.Increment(ref _rectAreaSequence);
        System.Threading.Volatile.Write(ref _rectAreaValid, 1);
        System.Threading.Interlocked.Increment(ref _rectAreaVersion);
    }

    internal void InvalidateRectSubmittedArea()
    {
        System.Threading.Interlocked.Increment(ref _rectAreaVersion);
        System.Threading.Volatile.Write(ref _rectAreaValid, 0);
        System.Threading.Volatile.Write(ref _rectAreaHasArea, 0);
        System.Threading.Volatile.Write(ref _rectAreaOpaqueInstances, 0);
        System.Threading.Volatile.Write(ref _rectAreaBlendedInstances, 0);
        System.Threading.Volatile.Write(ref _rectAreaTopCount, 0);
        System.Threading.Volatile.Write(ref _rectAreaOpaquePx2, 0.0);
        System.Threading.Volatile.Write(ref _rectAreaBlendedPx2, 0.0);
        System.Threading.Interlocked.Increment(ref _rectAreaVersion);
    }

    public bool TryCopyRectSubmittedAreaSample(Span<RectSubmittedAreaItem> blendedTop,
        out RectSubmittedAreaSample sample)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = System.Threading.Volatile.Read(ref _rectAreaVersion);
            if ((before & 1L) != 0) continue;
            int valid = System.Threading.Volatile.Read(ref _rectAreaValid);
            long sequence = System.Threading.Interlocked.Read(ref _rectAreaSequence);
            int opaqueInstances = System.Threading.Volatile.Read(ref _rectAreaOpaqueInstances);
            int blendedInstances = System.Threading.Volatile.Read(ref _rectAreaBlendedInstances);
            bool hasArea = System.Threading.Volatile.Read(ref _rectAreaHasArea) != 0;
            double opaque = System.Threading.Volatile.Read(ref _rectAreaOpaquePx2);
            double blended = System.Threading.Volatile.Read(ref _rectAreaBlendedPx2);
            int count = Math.Min(System.Threading.Volatile.Read(ref _rectAreaTopCount), blendedTop.Length);
            for (int i = 0; i < count; i++) blendedTop[i] = _rectAreaTop[i];
            long after = System.Threading.Volatile.Read(ref _rectAreaVersion);
            if (before != after || (after & 1L) != 0) continue;
            if (valid == 0 || sequence == 0) break;
            sample = new RectSubmittedAreaSample(unchecked((ulong)sequence), opaqueInstances, blendedInstances,
                hasArea, opaque, blended, count);
            return true;
        }
        sample = default;
        return false;
    }

    public void Resize(Size2 px) => Device.Resize(this, (uint)px.Width, (uint)px.Height);
    public void Present() => Device.Present(this);
    public bool PresentNoWait()
    {
        Device.Present(this, noWait: true);
        return !Frame.LastPresentRefused;
    }
    public void ConfigurePopupChrome(in PopupChromeMetrics m) => Backdrop?.ConfigureChrome(m.ContentRectPx, m.OpensUp, m.ClosedRatio, m.CornerRadiusPx);
    public void AnimatePopupOpen() => Backdrop?.AnimateOpen();
    public void AnimatePopupClose() => Backdrop?.AnimateClose();
    public bool PopupAnimating => Backdrop?.IsAnimating ?? false;
    public void Dispose() => Device.DisposeSwapchain(this);

    // ── Per-target seam members (Phase 1 §3.3) ──────────────────────────────────────────────────────────────────────
    // These used to be device-global IGpuDevice members (Suppress*Once/HintSettlePresent/LastFenceWaitMs/
    // TextRepaintPending/LastPresentStoodDown/LastPresentStats), backed by a single Activate-mirrored working copy —
    // a detached child's own suppression/timing could steal the main window's (or vice versa). Real storage is
    // `Frame` (TargetFrameState); the equivalent IGpuDevice members above still forward to the PRIMARY for callers
    // that have not yet moved to `_swapchain.X` (agent 1-D). Declared here so they satisfy `ISwapchain` once agent
    // 1-A adds the matching members there (Rhi.cs) — written against the plan's printed API, not yet cross-checked
    // against 1-A's actual interface text.
    public bool TextRepaintPending => Frame.TextRepaintPending;
    public bool LastPresentStoodDown => Frame.LastPresentStoodDown;
    /// <inheritdoc/>
    public bool IsOccluded => Frame.OccludedLatched || Frame.LastPresentStoodDown;
    public double LastFenceWaitMs => Frame.LastFenceWaitMs;
    public double LastLatencyWaitMs => Frame.LastLatencyWaitMs;
    public void GetLastSubmitWaits(out double latencyMs, out double bufferFenceMs)
    {
        latencyMs = Frame.LastSubmitLatencyWaitMs;
        bufferFenceMs = Frame.LastBufferFenceWaitMs;
    }
    public PresentStats LastPresentStats => Frame.LastPresentStats;
    public long PresentsDisplayed => System.Threading.Volatile.Read(ref Frame.PresentLedger.PresentsDisplayed);
    public long PresentsDropped => System.Threading.Volatile.Read(ref Frame.PresentLedger.PresentsDropped);
    public long VblanksRepeated => System.Threading.Volatile.Read(ref Frame.PresentLedger.VblanksRepeated);
    public void SuppressLatencyWaitOnce() => Frame.SkipLatencyOnce = true;
    public void SuppressVsyncOnce() => Frame.SkipVsyncOnce = true;
    public void HintSettlePresent() => Frame.HintSettlePresent = true;
    public void HintGeometryMotionPresent() => Frame.HintMotionFenceWait = true;

    /// <inheritdoc/>
    public void CompleteSettlePresent(bool blockUntilComposed)
    {
        if (!Frame.HintSettlePresent) return;
        Frame.HintSettlePresent = false;
        if (blockUntilComposed) _ = D3D12Device.DwmFlush();
    }
}
