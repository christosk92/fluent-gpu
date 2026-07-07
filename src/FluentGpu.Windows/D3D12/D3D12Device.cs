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
/// </summary>
public sealed unsafe class D3D12Device : IGpuDevice
{
    // Back buffers == per-frame allocators == frames-in-flight. Canon (budgets.md): 2 (FLIP_DISCARD); configurable 2–3.
    // Every site below keys off this, so 3 (more CPU run-ahead slack, +1 frame latency, +VRAM) is a one-line change.
    internal const uint FRAME_COUNT = 2;   // double-buffer (FLIP_DISCARD). Reverted from 3: triple-buffering correlated with a DXGI_ERROR_DEVICE_HUNG on the Adreno after sustained load (~6.5 min). The image-upload throttle (DecodeScheduler) is the safer spike fix.
    private const uint INFINITE = 0xFFFFFFFF;

    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private IDXGIFactory4* _factory;
    private IDXGISwapChain3* _swapChain;
    private ID3D12DescriptorHeap* _rtvHeap;
    private uint _rtvSize;
    private readonly ID3D12Resource*[] _backBuffers = new ID3D12Resource*[FRAME_COUNT];
    private readonly ID3D12CommandAllocator*[] _allocators = new ID3D12CommandAllocator*[FRAME_COUNT];
    private readonly ulong[] _frameFenceValues = new ulong[FRAME_COUNT];
    private ID3D12GraphicsCommandList* _cmdList;
    private ID3D12Fence* _fence;
    private ulong _fenceValue;
    private HANDLE _fenceEvent;
    private HANDLE _frameLatencyWaitable;   // signaled when the swapchain can accept a new frame — bounds queued-frame latency
    private bool _hasLatencyWaitable;
    private uint _swapChainFlags;           // flags the swapchain was created with (must be preserved across ResizeBuffers)
    private bool _tearingSupported;         // DXGI_FEATURE_PRESENT_ALLOW_TEARING (hwnd, vsync-off path only)
    private bool _vsync = !FluentGpu.Foundation.Diag.EnvFlag("FG_NOVSYNC");  // FG_NOVSYNC=1 forces interval 0 (diagnose present-cap vs frame-cost). Present sync-interval 1 when true; interval 0 + ALLOW_TEARING when false
    private static readonly bool s_dred = FluentGpu.Foundation.Diag.EnvFlag("FG_D3D12_DRED");

    private HWND _hwnd;
    private uint _w, _h;
    private uint _frameIndex;
    private D3D12Swapchain? _primarySwapchain;
    private D3D12Swapchain? _activeSwapchain;
    private readonly List<D3D12Swapchain> _swapchains = new(2);
    private SdfSharedResources? _sdf;
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
    private readonly List<RectF> _clipStack = new(16);
    // Tier-2 rounded clip (E9), parallel to _clipStack: the innermost rounded-box clip in effect (W <= 0 = none).
    // A rounded PushClip replaces it; a plain (rectangular) PushClip inherits the enclosing rounded clip — a reveal
    // rect nested inside a rounded surface keeps clipping the surface's corners. RoundRect-pipeline instances carry
    // the current entry; other pipelines stay scissor-only (the honest scope documented on ClipCmd).
    private readonly List<(RectF Rect, float Radius)> _roundedClipStack = new(16);
    private AcrylicCompositor? _acrylic;
    private OpacityLayerCompositor? _opacity;
    // ALL layered primary-stream frames composite DIRECTLY on the back buffer (no full-window offscreen canvas + blit).
    // Opacity/blur/edge-fade groups blend over the back buffer (the blur samples its own offscreen group RT); an ACRYLIC
    // snapshots only the small back-buffer region under it (AcrylicCompositor.SnapshotBackBufferRegion → bit-exact region
    // copy → blur → composite). An open lyrics rail (blur layers) or a frosted in-app pill/bar therefore no longer forces
    // a whole-window round-trip every frame — the cost grew with window area (measured 5.6ms→2.3ms submit at 1440p).
    // Cross-frame blur cache — ALWAYS ON (no flag). A stationary self-blur reuses last frame's retained, already-blurred
    // pixels (a small region pin), skipping the subtree render + BOTH separable Gaussian passes. This is what keeps the
    // full dist≤6 lyrics depth-of-field (~13 self-blur layers) cheap while the MAIN TRACK LIST scrolls: the lyrics rail is
    // a separate, stationary viewport, so its blur layers keep a recurring content+position hash frame-to-frame ⇒ pin ⇒
    // HIT (a bit-exact CopyTextureRegion composite, not a re-blur). A HIT is pixel-identical to a MISS, and the recurrence
    // gate (below) only pins content that was already byte-identical last frame, so stationary content never
    // pin/miss-alternates (no flicker). The lyrics' OWN scroll (content moves ⇒ fresh hash ⇒ miss) is handled separately
    // by the recorder's scroll-defer (it drops the blur entirely while that viewport is in motion).
    private int _blurCacheHit, _blurCacheMiss;   // per-frame diagnostics (Diag "d3d12" blurCacheHit/Miss)
    private int _blurHoldHit, _blurHoldFallback; // per-frame diagnostics for hold-if-cached blur layers
    // Blur-cache recurrence test: a self-blur is PINNED (retained) on a miss ONLY when its content hash recurred from the
    // PREVIOUS frame (⇒ a STATIONARY surface that will hit next frame). Scrolling/animating content has a fresh hash every
    // frame ⇒ never recurs ⇒ renders into a transient slot ⇒ no per-layer distinct-RT churn (pinning every scroll miss
    // would give each of ~13 layers its own canvas-sized RT and thrash memory bandwidth). Two swapped rings, alloc-free.
    private ulong[] _curBlurHashes = new ulong[64];
    private ulong[] _lastBlurHashes = new ulong[64];
    private int _curBlurHashCount, _lastBlurHashCount;
    // Open PushLayer kinds in stream order (acrylic pops are no-ops; opacity + self-blur pops composite their leased
    // RT), and the leased (slot, alpha, σ) per open OPACITY/BLUR group — both reused across frames (0 steady alloc). A
    // blur group is an opacity group with Sigma > 0: its RT is separable-Gaussian-blurred before the flat composite.
    private const int NoopLayerKind = -1;
    private readonly List<int> _layerKinds = new(8);
    private readonly List<(int Slot, PushLayerCmd L, ulong PinHash)> _opacityGroups = new(4);

    public int LastBlurCacheHit => _blurCacheHit;
    public int LastBlurCacheMiss => _blurCacheMiss;
    public int LastBlurHoldHit => _blurHoldHit;
    public int LastBlurHoldFallback => _blurHoldFallback;
    public int LastOpacityGroups => _opacity?.GroupsThisFrame ?? 0;
    private GlyphRenderer? _glyphs;
    private ImageTextureStore? _imageTextures;
    private ImagePipeline? _imagePipe;
    private readonly List<(ImageInstance inst, int imageId)> _imageDraws = new();
    private readonly List<ImageInstance> _imageRangeScratch = new(64);
    // Painter-order draw runs: a segment's non-glyph primitives in STREAM order (consecutive same-kind ops batched into
    // one draw), so a shadow correctly sits OVER the background drawn before it and UNDER the element it belongs to.
    // Without this, all shadows batch before all rects and any opaque background paints over them. Glyphs always draw last.
    private enum PrimKind : byte { Rect, Shadow, Gradient, Image, Arc, Polyline }
    private readonly List<(PrimKind Kind, int Count)> _runs = new();
    private int _frameImageCount;
    private int _frameImageSkipped;
    private readonly List<GlyphInstance> _glyphInsts = new();
    private readonly List<GradGlyphInstance> _gradGlyphInsts = new();   // sub-glyph karaoke wipe (active lyric line + glow)
    private float _frameScale = 1f;
    private int _frameRectCount;
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
    private enum BoundPipe : byte { None, Rect, Shadow, Arc, Polyline, Gradient, Glyph, GradGlyph, Image }
    private BoundPipe _boundPipe;
    private bool _sharedSdfStateBound;
    private RECT _lastScissor;
    private RECT _desiredScissor;
    private bool _scissorValid;
    private bool _desiredScissorValid;
    private int _framePipeBinds, _framePipeBindsSkipped;      // PSO/shared-state binds vs runs that reused bound state
    private int _frameScissorSets, _frameScissorSkipped;      // RSSetScissorRects recorded vs deduped
    private int _frameSegments, _frameRuns;                   // FlushSegment calls / painter-order runs replayed
    private int _frameClipOps, _frameLayerOps;                // Push/PopClip and Push/PopLayer ops decoded
    private readonly StringTable _strings;
    private readonly bool _composited;

    private void InvalidateCmdState() { _boundPipe = BoundPipe.None; _sharedSdfStateBound = false; _scissorValid = false; }

    // DirectComposition (Mica path): the swapchain is composed onto the HWND so DWM's Mica shows through transparent pixels.
    private IDCompositionDevice* _dcomp;
    private IDCompositionTarget* _dcompTarget;
    private IDCompositionVisual* _dcompVisual;

    public D3D12Device(StringTable strings, bool composited = false)
    {
        _strings = strings;
        _composited = composited;
    }

    public string BackendNameSuffix { get; private set; } = "";
    public string BackendName => "D3D12" + BackendNameSuffix;
    // Secondary popup swapchains (OS-acrylic windowed menus) submit + present on the shared device/queue alongside the
    // main window. The earlier DEVICE_REMOVED came from the shared full-window acrylic/opacity canvas being released
    // without a fence when popup + main submits alternated sizes; popups now stay on the streaming path (SubmitDrawList)
    // and never touch that canvas. Per-target allocator/instance-bank reuse is already serialized by WaitForFrame(index).
    public bool SupportsSecondarySwapchains => true;

    /// <summary>Tracked live D3D12 resource totals (bytes + count) from <see cref="D3D12MemoryDiagnostics"/> — an O(1)
    /// read of the running tally for the MemCensus sampler.</summary>
    public (long bytes, int count) DiagResourceTotals => D3D12MemoryDiagnostics.LiveTotals();

    /// <summary>One-line GPU residency summary (glyph atlas + image texture store) for the MemCensus
    /// <c>GpuDetail</c> hook. Reads the stores' census accessors; null until the device is initialized. Tiny
    /// fixed-bucket sums (never per-frame).</summary>
    public string DiagGpuDetail =>
        _glyphs is null || _imageTextures is null
            ? ""
            : $"glyphs={_glyphs.CachedGlyphCount} runs={_glyphs.CachedRunCount} atlasGen={_glyphs.AtlasResetCount} quadPool={_glyphs.QuadPoolRetained}" +
              $" | tex: atlas={_imageTextures.AtlasImageCount} pages={_imageTextures.AtlasPageCount} pooledFree={_imageTextures.PooledTextureCount} retired={_imageTextures.RetiredCount}" +
              $" srv={_imageTextures.DescriptorSlotsUsed}/{_imageTextures.DescriptorCapacity} high={_imageTextures.DescriptorHighWater} rejected={_imageTextures.DroppedThisRun}";

    /// <summary>Operator dump: live D3D12 resources aggregated by name prefix, largest first (to stderr). The empirical
    /// "which resource class holds the climbing RAM" probe for native/UMA leak hunts. Routes to <see cref="D3D12MemoryDiagnostics"/>.</summary>
    public void DiagDumpLive(string label) => D3D12MemoryDiagnostics.DumpLive(label);

    internal ID3D12Device* Device => _device;
    internal ID3D12GraphicsCommandList* CommandList => _cmdList;

    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        if (_device == null) InitDevice();
        EnsurePipelines();

        bool composited = desc.Composited || (_primarySwapchain is null && _composited);
        var target = new D3D12Swapchain(this, (HWND)desc.PresentTarget.Value,
            (uint)Math.Max(1, (int)desc.SizePx.Width), (uint)Math.Max(1, (int)desc.SizePx.Height), composited,
            desc.DesktopAcrylic, desc.AcrylicTint, desc.CornerRadiusPx);
        InitSwapChain(target);
        _swapchains.Add(target);
        _primarySwapchain ??= target;
        Activate(target);
        return target;
    }

    private void EnsurePipelines()
    {
        if (_rectPipe is not null) return;
        _sdf = new SdfSharedResources();
        _sdf.Init(_device);
        _rectPipe = new RoundRectPipeline();
        _rectPipe.Init(_device, _sdf);
        _shadowPipe = new ShadowPipeline();
        _shadowPipe.Init(_device, _sdf);
        _arcPipe = new ArcPipeline();
        _arcPipe.Init(_device, _sdf);
        _polylinePipe = new PolylineStrokePipeline();
        _polylinePipe.Init(_device, _sdf);
        _gradPipe = new GradientPipeline();
        _gradPipe.Init(_device, _sdf);
        _acrylic = new AcrylicCompositor();
        _acrylic.Init(_device);
        _opacity = new OpacityLayerCompositor();
        _opacity.Init(_device);
        _glyphs = new GlyphRenderer();
        _glyphs.SetLivenessSource(_strings);   // reclaimed text ids → prompt run-cache eviction (quad-array recycling)
        _glyphs.Init(_device);
        _imageTextures = new ImageTextureStore();
        _imageTextures.Init(_device);
        _imagePipe = new ImagePipeline();
        _imagePipe.Init(_device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private static void SetName(ID3D12Device* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12CommandQueue* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12CommandAllocator* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12GraphicsCommandList* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }
    private static void SetName(ID3D12Fence* obj, string name) { if (obj != null) { fixed (char* p = name) _ = obj->SetName(p); } }

    private static void ConfigureDred()
    {
        if (!s_dred) return;
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
    // submit/present — i.e. when AppHost spawns it for FG_RENDER_THREAD (force-sync) or FG_RENDER_ASYNC. While armed, a
    // SubmitDrawList/Present from any thread but Render throws deterministically UNDER FGGUARD, so a stray UI-side GPU
    // touch in async is caught in CI, never shipped. Inert in the default single-thread build (_renderConfined stays
    // false ⇒ the assert is a no-op), and the whole thing erases in Release (the [Conditional] + ThreadGuard vanish).
    private bool _renderConfined;
    public void MarkRenderConfined() => _renderConfined = true;
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertSubmitThread() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRender(); }

    // Seam Step 1 (ASYNC only): image Stage/Free/FlushUploads become render-confined once the host wires the upload
    // queue. Force-sync leaves the store UI-staged (no overlap), so this is a SEPARATE arm from MarkRenderConfined.
    public void MarkImageUploadsRenderConfined() => _imageTextures?.MarkRenderConfined();

    // Seam Step 1 (ASYNC only): drain the UI→render image-upload queue on the render thread, just before the frame's
    // SubmitDrawList opens its command list (so a staged texture is resident before the draw that references it). Every
    // Stage/Free/return-to-pool here runs render-confined → the texture store is single-toucher, no lock.
    public void DrainImageJobs(FluentGpu.Hosting.Threading.ImageUploadQueue queue)
    {
        AssertSubmitThread();
        if (_imageTextures is null) return;
        while (queue.TryDequeueJob(out var j))
        {
            if (j.Evict) { _imageTextures.Free(j.Id); continue; }
            var res = j.Buffer is null ? ImageUploadResult.Invalid : _imageTextures.Stage(j.Id, j.Buffer.AsSpan(0, j.ByteLen), j.W, j.H);
            if (res != ImageUploadResult.Accepted) queue.PostReject(j.Id, res);   // +1-frame async admission: the UI folds the rejection next Pump
            if (j.Buffer is not null) System.Buffers.ArrayPool<byte>.Shared.Return(j.Buffer);   // ownership transferred to us; return after Stage copied it
        }
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
        if (_device == null) return false;
        int reason = (int)_device->GetDeviceRemovedReason();
        if (reason != 0) { System.Threading.Volatile.Write(ref _deviceLostReason, reason); return true; }
        return false;
    }

    public void DumpDeviceLostDiagnostics(Action<string> write)
    {
        if (write is null) return;
        int recorded = System.Threading.Volatile.Read(ref _deviceLostReason);
        uint reason = _device == null ? 0u : (uint)_device->GetDeviceRemovedReason();
        var (bytes, count) = D3D12MemoryDiagnostics.LiveTotals();
        write($"[d3d12] device-lost recorded=0x{(uint)recorded:X8} currentReason=0x{reason:X8} backend={BackendName} liveResources={count} liveBytes={bytes}");
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
    // bail (recording the reason) if the device died. Only bails on ACTUAL removal — a merely-slow GPU keeps waiting, so
    // we never return early and read an unfinished back buffer.
    private void WaitFenceEventBounded()
    {
        while (true)
        {
            if (WaitForSingleObject(_fenceEvent, 1000) == 0u) return;   // WAIT_OBJECT_0 — fence signaled
            int reason = (int)_device->GetDeviceRemovedReason();
            if (reason != 0) { System.Threading.Volatile.Write(ref _deviceLostReason, reason); return; }
        }
    }

    // Test hook (FG_FORCE_DEVICE_LOST=<frameN>): force a clean DEVICE_REMOVED via ID3D12Device5::RemoveDevice — a
    // controlled removal that does NOT TDR the whole desktop — to exercise the async recovery rendezvous on real hardware.
    public void InjectDeviceLost()
    {
        ID3D12Device5* dev5;
        if (_device != null && (int)_device->QueryInterface(__uuidof<ID3D12Device5>(), (void**)&dev5) >= 0 && dev5 != null)
        {
            dev5->RemoveDevice();
            dev5->Release();
        }
    }

    private void InitDevice()
    {
        uint flags = 0;
#if DEBUG
        if (Diag.EnvFlag("FG_D3D12_DEBUG"))
        {
            ID3D12Debug* dbg = null;
            if ((int)D3D12GetDebugInterface(__uuidof<ID3D12Debug>(), (void**)&dbg) >= 0 && dbg != null)
            {
                dbg->EnableDebugLayer();
                dbg->Release();
            }
            flags |= DXGI.DXGI_CREATE_FACTORY_DEBUG;
        }
#endif
        ConfigureDred();
        IDXGIFactory4* factory;
        Check(CreateDXGIFactory2(flags, __uuidof<IDXGIFactory4>(), (void**)&factory), "CreateDXGIFactory2");
        _factory = factory;

        ID3D12Device* device = null;
        HRESULT hr = D3D12CreateDevice(null, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device);
        if ((int)hr < 0)
        {
            // WARP fallback (VMs / RDP / no hardware GPU) — per pal-rhi.md §3b.1.
            IDXGIAdapter* warp;
            Check(_factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&warp), "EnumWarpAdapter");
            Check(D3D12CreateDevice((IUnknown*)warp, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device),
                "D3D12CreateDevice(WARP)");
            warp->Release();
            BackendNameSuffix = " (WARP)";
        }
        _device = device;
        SetName(_device, "FluentGpu.Device");

        // Publish a coarse GPU power tier (GpuProfile) so UI quality defaults can scale to the hardware WITHOUT a render-
        // hardware seam contract. UMA == integrated/APU (and WARP) — shares system RAM, a fraction of a discrete GPU's
        // fill rate / bandwidth ⇒ Weak; a GPU with dedicated VRAM ⇒ Strong. Best-effort: any failure leaves the tier
        // Unknown, which callers treat as the balanced default. This is what lets the lyrics depth-of-field stay smooth
        // on a weak iGPU (it auto-selects the cheap path) while a discrete GPU keeps the full effect.
        try
        {
            D3D12_FEATURE_DATA_ARCHITECTURE arch = default;
            if ((int)_device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE, &arch, (uint)sizeof(D3D12_FEATURE_DATA_ARCHITECTURE)) >= 0)
                FluentGpu.Foundation.GpuProfile.Tier = arch.UMA != 0
                    ? FluentGpu.Foundation.GpuPowerTier.Weak
                    : FluentGpu.Foundation.GpuPowerTier.Strong;
        }
        catch { /* detection is best-effort; Unknown ⇒ balanced default */ }

        D3D12_COMMAND_QUEUE_DESC qd = default;
        qd.Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT;
        qd.Flags = D3D12_COMMAND_QUEUE_FLAGS.D3D12_COMMAND_QUEUE_FLAG_NONE;
        ID3D12CommandQueue* queue;
        Check(_device->CreateCommandQueue(&qd, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
        _queue = queue;
        SetName(_queue, "FluentGpu.CommandQueue");

        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            ID3D12CommandAllocator* alloc;
            Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator");
            _allocators[i] = alloc;
            SetName(alloc, $"FluentGpu.CommandAllocator[{i}]");
        }

        ID3D12GraphicsCommandList* list;
        Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, _allocators[0], null,
            __uuidof<ID3D12GraphicsCommandList>(), (void**)&list), "CreateCommandList");
        _cmdList = list;
        SetName(_cmdList, "FluentGpu.CommandList");
        _cmdList->Close();   // start closed; opened each frame after Reset

        ID3D12Fence* fence;
        Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence), "CreateFence");
        _fence = fence;
        SetName(_fence, "FluentGpu.FrameFence");
        _fenceValue = 0;
        _fenceEvent = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);

        _rtvSize = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
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
        sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
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
        Check(target.SwapChain->SetMaximumFrameLatency(FRAME_COUNT - 1), "SetMaximumFrameLatency");
        target.FrameLatencyWaitable = target.SwapChain->GetFrameLatencyWaitableObject();
        target.HasLatencyWaitable = target.FrameLatencyWaitable != HANDLE.NULL;

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
        target.FrameIndex = target.SwapChain->GetCurrentBackBufferIndex();
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
        IDCompositionVisual* visual;
        Check(_dcomp->CreateVisual(&visual), "CreateVisual");
        target.DcompVisual = visual;
        Check(target.DcompVisual->SetContent((IUnknown*)target.SwapChain), "Visual.SetContent");
        Check(target.DcompTarget->SetRoot(target.DcompVisual), "Target.SetRoot");
        Check(_dcomp->Commit(), "DComp.Commit");
        target.DcompBindPending = false;
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

    private void Activate(D3D12Swapchain target)
    {
        _activeSwapchain = target;
        _hwnd = target.Hwnd;
        _w = target.W;
        _h = target.H;
        _frameIndex = target.FrameIndex;
        _swapChain = target.SwapChain;
        _rtvHeap = target.RtvHeap;
        _frameLatencyWaitable = target.FrameLatencyWaitable;
        _hasLatencyWaitable = target.HasLatencyWaitable;
        _swapChainFlags = target.SwapChainFlags;
        _tearingSupported = target.TearingSupported;
        for (uint i = 0; i < FRAME_COUNT; i++)
            _backBuffers[i] = target.BackBuffers[i];
    }

    private void StoreActive()
    {
        if (_activeSwapchain is not { } target) return;
        target.W = _w;
        target.H = _h;
        target.FrameIndex = _frameIndex;
        target.SwapChain = _swapChain;
        target.RtvHeap = _rtvHeap;
        target.FrameLatencyWaitable = _frameLatencyWaitable;
        target.HasLatencyWaitable = _hasLatencyWaitable;
        target.SwapChainFlags = _swapChainFlags;
        target.TearingSupported = _tearingSupported;
        for (uint i = 0; i < FRAME_COUNT; i++)
            target.BackBuffers[i] = _backBuffers[i];
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        if (_primarySwapchain is null) throw new InvalidOperationException("CreateSwapchain must be called before SubmitDrawList.");
        SubmitDrawList(drawList, sortKeys, in ctx, _primarySwapchain);
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
    {
        if (target is not D3D12Swapchain sc || sc.Device != this || sc.Disposed)
            throw new InvalidOperationException("Submit target is not a live D3D12 swapchain from this device.");
        AssertSubmitThread();   // seam Step 0: when render-confined (force-sync/async), only the render thread may submit
        Activate(sc);
        // Normally throttle to present cadence before producing this frame. A keep-alive repaint fired from inside an
        // OS modal move/size loop (host called SuppressLatencyWaitOnce) skips it so the WndProc thread isn't blocked
        // up to a vblank — the drag-start/live-resize hitch. Self-resetting: one suppressed wait per call.
        // Time the two BLOCKING GPU-fence waits (latency waitable + frame fence) separately — this UI-thread stall is the
        // bulk of measured "submit" (FrameStats.FenceWaitMs); the render-thread seam will move it off this thread.
        long fenceWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_skipLatencyOnce) _skipLatencyOnce = false;
        else WaitForLatency();   // bound queued-frame latency before starting this frame's production
        _frameIndex = _swapChain->GetCurrentBackBufferIndex();
        WaitForFrame(_frameIndex);
        _lastFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(fenceWaitStart).TotalMilliseconds;
        ID3D12CommandAllocator* allocator = _allocators[_frameIndex];
        Check(allocator->Reset(), "allocator.Reset");
        Check(_cmdList->Reset(allocator, null), "cmdList.Reset");
        InvalidateCmdState();   // fresh command list — nothing is bound
        _imageTextures?.FlushUploads(_cmdList);

        ID3D12Resource* backBuffer = _backBuffers[_frameIndex];
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += _frameIndex * _rtvSize;

        // DPI: render at native resolution but map DIP coordinates via a LOGICAL viewport (= physical / scale),
        // while glyphs are rasterized at physical px → crisp + correctly sized at high DPI.
        _frameScale = ctx.Scale <= 0f ? 1f : ctx.Scale;
        _frameRectCount = 0;
        _frameGlyphInstanceCount = 0;
        _frameImageCount = 0;
        _frameImageSkipped = 0;
        _framePipeBinds = 0; _framePipeBindsSkipped = 0;
        _frameScissorSets = 0; _frameScissorSkipped = 0;
        _frameSegments = 0; _frameRuns = 0; _frameClipOps = 0; _frameLayerOps = 0;
        _blurCacheHit = 0; _blurCacheMiss = 0; _blurHoldHit = 0; _blurHoldFallback = 0;
        (_lastBlurHashes, _curBlurHashes) = (_curBlurHashes, _lastBlurHashes);   // rotate the blur-cache recurrence ring
        _lastBlurHashCount = _curBlurHashCount; _curBlurHashCount = 0;
        // Every pipe banks its instance upload buffer by back-buffer index: WaitForFrame above fenced the submit that
        // last USED this index (frame N-2), so writing this bank can never race frame N-1's still-in-flight GPU reads —
        // the CPU↔GPU tear that flickered on scroll/hover (the image pipe had this; the six geometry pipes were missed).
        _rectPipe!.BeginFrame((int)_frameIndex);
        _shadowPipe!.BeginFrame((int)_frameIndex);
        _arcPipe!.BeginFrame((int)_frameIndex);
        _polylinePipe!.BeginFrame((int)_frameIndex);
        _gradPipe!.BeginFrame((int)_frameIndex);
        _glyphs!.BeginFrame((int)_frameIndex);
        _imagePipe!.BeginFrame((int)_frameIndex);
        float lw = _w / _frameScale, lh = _h / _frameScale;

        // Only the PRIMARY (main-window) swapchain runs the layered compositor. The acrylic/opacity compositors keep ONE
        // shared full-window canvas sized to the active swapchain and release it WITHOUT a GPU fence on a size change
        // (EnsureSize assumes size changes only follow a fenced swapchain Resize). A secondary popup swapchain submitting
        // at a different size would destroy that canvas while the main window's in-flight GPU work still references it →
        // use-after-free → DXGI_ERROR_DEVICE_REMOVED on the next Present (the prior popup-window crash). OS-backed popups
        // are transparent (DWM supplies the acrylic), so they carry no engine layers; force them onto the streaming path
        // — never touching the shared canvas. A stray PushLayer in a popup stream is harmlessly ignored (drawn flat).
        // Three submit routes for the PRIMARY swapchain: (0) NO layers → SubmitStreaming straight to the back buffer; (1)
        // opacity/blur/edge-fade layers but NO acrylic → render the scene + composite those layers DIRECTLY on the back
        // buffer (FG_BACKBUFFER_LAYERS): those kinds composite OVER a target without sampling it (the blur reads its own
        // offscreen group RT, not the canvas), so the full-window offscreen canvas clear + blit is unnecessary — an open
        // lyrics rail (blur layers) then does NOT tax the whole window's scrolling/animation; (2) an ACRYLIC layer IS
        // present → the canvas path (acrylic must snapshot the backdrop offscreen to blur it): clear the canvas, render the
        // scene into it, run the layer passes, blit back.
        int layerKind = sc == _primarySwapchain ? StreamLayerKind(drawList) : 0;
        if (layerKind != 0)
        {
            // Both opacity/blur (kind 1) and acrylic (kind 2) composite directly on the back buffer — no offscreen canvas
            // + blit. An acrylic keeps the canvas only as its region-copy scratch (SnapshotBackBufferRegion), so size it
            // when one is present in the stream.
            if (layerKind == 2) _acrylic!.EnsureSize(_w, _h);
            _opacity!.EnsureSize(_w, _h);
            SubmitWithLayers(drawList, ctx, lw, lh, rtv, directToBackBuffer: true);
        }
        else
        {
            // GEN-COM (WIRED): the generated hand-vtable calli binding replaces the TerraFX method call. TerraFX still
            // supplies the ID3D12Fence* type; the vtable[8] calli is emitted from d3d12.comabi.json (no human-typed slot).
            _acrylic?.TickIdle(global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence));   // age/trim layer RT pools
            _opacity?.TickIdle(global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence));
            _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
            float* clear = stackalloc float[4] { ctx.Clear.R, ctx.Clear.G, ctx.Clear.B, ctx.Clear.A };
            _cmdList->ClearRenderTargetView(rtv, clear, 0, null);
            SetFullViewport();
            SubmitStreaming(drawList, lw, lh);
        }
        Diag.Set("d3d12", "acrylicLayers", _acrylic?.LayersThisFrame ?? 0);   // PushLayer composites this frame
        Diag.Set("d3d12", "acrylicPoolRts", _acrylic?.PooledRtCount ?? 0);    // live pooled layer RTs (steady state: 2 while a surface is open)
        Diag.Set("d3d12", "opacityGroups", _opacity?.GroupsThisFrame ?? 0);   // flat opacity groups composited this frame
        Diag.Set("d3d12", "opacityPoolRts", _opacity?.PooledRtCount ?? 0);    // live pooled group RTs (≈ nesting depth while fading)
        Diag.Set("d3d12", "blurCacheHit", _blurCacheHit);                     // blur layers served from the cross-frame pin cache this frame
        Diag.Set("d3d12", "blurCacheMiss", _blurCacheMiss);                   // blur layers re-rendered+re-blurred (content/position/σ changed)
        Diag.Set("d3d12", "blurHoldHit", _blurHoldHit);
        Diag.Set("d3d12", "blurHoldFallback", _blurHoldFallback);
        Diag.Set("d3d12", "segments", _frameSegments);                        // non-empty FlushSegment calls (clips flush only when pending draws need the old scissor)
        Diag.Set("d3d12", "runs", _frameRuns);                                // painter-order runs replayed across all segments
        Diag.Set("d3d12", "clipOps", _frameClipOps);                          // Push/PopClip ops decoded this frame
        Diag.Set("d3d12", "layerOps", _frameLayerOps);                        // Push/PopLayer ops decoded this frame
        Diag.Set("d3d12", "pipeBinds", _framePipeBinds);                      // PSO/shared-state binds recorded
        Diag.Set("d3d12", "pipeBindsSkipped", _framePipeBindsSkipped);        // runs that reused the cross-segment bound state
        Diag.Set("d3d12", "scissorSets", _frameScissorSets);                  // RSSetScissorRects recorded
        Diag.Set("d3d12", "scissorSkipped", _frameScissorSkipped);            // scissor sets deduped (rect unchanged)
        Diag.Set("d3d12", "instancesDropped", DroppedInstanceCount());        // instance-buffer overflow visibility
        Diag.Set("d3d12", "rects", _frameRectCount);
        Diag.Set("d3d12", "glyphInstances", _frameGlyphInstanceCount);
        Diag.Set("d3d12", "images", _frameImageCount);
        Diag.Set("d3d12", "imagesSkipped", _frameImageSkipped);   // >0 ⇒ a recorded image had no live texture this frame
        Diag.Set("d3d12", "imageAtlas", _imageTextures!.AtlasImages);   // thumbnails (<=128) packed into shared atlas pages
        Diag.Set("d3d12", "imagePool", _imageTextures.PoolImages);      // art (256/512) in reused per-bucket pool textures
        Diag.Set("d3d12", "imageSrvUsed", _imageTextures.DescriptorSlotsUsed);
        Diag.Set("d3d12", "imageSrvHighWater", _imageTextures.DescriptorHighWater);
        Diag.Set("d3d12", "imageUploadRejected", _imageTextures.DroppedThisRun);
        Diag.Set("text.atlas", "cachedGlyphs", _glyphs!.CachedGlyphs);
        Diag.Set("text.atlas", "nonZeroBytes", _glyphs.AtlasNonZero);
        Diag.Set("text.run", "cachedRuns", _glyphs.CachedRuns);      // shaped runs held across frames
        Diag.Set("text.run", "runsCached", _glyphs.RunsCached);      // this frame: runs served from the shaped-run cache
        Diag.Set("text.run", "runsShaped", _glyphs.RunsShaped);      // this frame: runs (re)shaped — should be ~0 in steady state

        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        Check(_cmdList->Close(), "cmdList.Close");

        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, (void**)&execList);   // GEN-COM (wired)
        SignalFrame(_frameIndex);
        StoreActive();
    }

    // Decode completion → resident GPU texture (media-pipeline §4.1). Staged here (heap/upload only, no command list);
    // the CopyTextureRegion is recorded by FlushUploads at the top of the next SubmitDrawList.
    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
        => _ = TryUploadImage(imageId, pbgra8, w, h);

    public ImageUploadResult TryUploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
        => _imageTextures?.Stage(imageId, pbgra8, w, h) ?? ImageUploadResult.Invalid;

    // Residency evicted the image → free its GPU texture (deferred behind the frame fence in the store).
    public void EvictImage(int imageId) => _imageTextures?.Free(imageId);

    private void ClearInsts() { _rectInsts.Clear(); _glyphInsts.Clear(); _gradGlyphInsts.Clear(); _shadowInsts.Clear(); _arcInsts.Clear(); _polylineInsts.Clear(); _gradInsts.Clear(); _imageDraws.Clear(); _runs.Clear(); }

    // Record (or extend) a painter-order run for the just-appended primitive, so RecordAll can replay in stream order.
    private void PushRun(PrimKind kind)
    {
        int n = _runs.Count;
        if (n > 0 && _runs[n - 1].Kind == kind) _runs[n - 1] = (kind, _runs[n - 1].Count + 1);
        else _runs.Add((kind, 1));
    }

    private void Decode(ReadOnlySpan<byte> cmds)
    {
        ClearInsts();
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
                    string s = _strings.Resolve(g.Text);
                    if (s.Length > 0)
                    {
                        int before = _glyphInsts.Count;
                        _glyphs!.LayoutRun(g.Text, g.Family, s, _strings.Resolve(g.Family), g.FontSize, g.Weight, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines,
                            g.CharSpacing, g.LineHeight, g.LineStacking, g.LineBounds, g.Color, _frameScale, g.Transform, g.Opacity, _glyphInsts,
                            g.SpanRunId, g.ForceColor != 0, g.InMotion != 0);
                        _frameGlyphInstanceCount += _glyphInsts.Count - before;
                    }
                    break;
                }
                case DrawOp.DrawGlyphRunGradient:
                {
                    var g = MemoryMarshal.Read<DrawGlyphRunGradientCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>();
                    string s = _strings.Resolve(g.Text);
                    if (s.Length > 0)
                    {
                        int before = _gradGlyphInsts.Count;
                        // Karaoke wipe: a per-PIXEL sub-glyph gradient (before→after over a soft band at the split) via a
                        // SEPARATE gradient PSO/batch (_gradGlyphInsts) — normal text keeps its lean single-color path.
                        _glyphs!.LayoutRunGradient(g.Text, g.Family, s, _strings.Resolve(g.Family), g.FontSize, g.Weight, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines,
                            g.CharSpacing, g.LineHeight, g.LineStacking, g.LineBounds, g.Before, g.After, g.Split, g.Softness, g.Lift, _frameScale, g.Transform, g.Opacity, _gradGlyphInsts,
                            g.SpanRunId, g.InMotion != 0);
                        _frameGlyphInstanceCount += _gradGlyphInsts.Count - before;
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
                case DrawOp.DrawImage:
                {
                    var im = MemoryMarshal.Read<DrawImageCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawImageCmd>();
                    // Draw whatever texture is resident under this id — the BlurHash LQIP preview (uploaded at request)
                    // OR the full-res art (which replaces it on decode). Flat tint only when no texture exists yet.
                    if (_imageTextures!.Has(im.ImageId)) AddReadyImage(in im);
                    else AddImagePlaceholder(in im);
                    break;
                }
                case DrawOp.DrawRoundRectStroke:
                {
                    var c = MemoryMarshal.Read<DrawRoundRectStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>();
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
                case DrawOp.DrawGradientRect:
                {
                    var c = MemoryMarshal.Read<DrawGradientRectCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGradientRectCmd>();
                    _gradInsts.Add(new GradientInstance
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
                    });
                    PushRun(PrimKind.Gradient);
                    break;
                }
                case DrawOp.DrawGradientStroke:
                {
                    var c = MemoryMarshal.Read<DrawGradientStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGradientStrokeCmd>();
                    _gradInsts.Add(new GradientInstance
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
                    });
                    PushRun(PrimKind.Gradient);
                    break;
                }
                case DrawOp.PushLayer:
                    pos += Unsafe.SizeOf<PushLayerCmd>();         // wired to the backdrop subsystem (phase 5)
                    break;
                case DrawOp.PopLayer:
                    pos += Unsafe.SizeOf<PopLayerCmd>();
                    break;
                default:
                    pos = cmds.Length;
                    break;
        }
        return pos;
    }

    private void AddImagePlaceholder(in DrawImageCmd im)
    {
        _rectInsts.Add(new RectInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            RTL = im.Radii.TopLeft, RTR = im.Radii.TopRight, RBR = im.Radii.BottomRight, RBL = im.Radii.BottomLeft,
            R = im.Placeholder.R, G = im.Placeholder.G, B = im.Placeholder.B, A = im.Placeholder.A,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy, Opacity = im.Opacity,
        });
        _frameRectCount++;
        PushRun(PrimKind.Rect);
    }

    // Ready image: sample the resident GPU texture through the ImagePipeline. It draws ABOVE the card/section
    // background rects but BELOW glyph labels (see RecordAll ordering). CrossFade=1 ⇒ full image; the placeholder→image
    // fade (M4) just drives CrossFade 0→1 — the shader already lerps placeholder→sampled in premultiplied space.
    private void AddReadyImage(in DrawImageCmd im)
    {
        _imageDraws.Add((new ImageInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            RTL = im.Radii.TopLeft, RTR = im.Radii.TopRight, RBR = im.Radii.BottomRight, RBL = im.Radii.BottomLeft,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy,
            Opacity = im.Opacity, CrossFade = im.CrossFade,
            PR = im.Placeholder.R, PG = im.Placeholder.G, PB = im.Placeholder.B, PA = im.Placeholder.A,
            // Content-fit sub-rect (ImageFit.Cover etc.) in 0..1 source space; composed with the atlas cell at draw time.
            UvX = im.UvRect.X, UvY = im.UvRect.Y, UvW = im.UvRect.W, UvH = im.UvRect.H,
        }, im.ImageId));
        _frameImageCount++;
        PushRun(PrimKind.Image);
    }

    private void SetFullViewport()
    {
        D3D12_VIEWPORT vpd = new() { TopLeftX = 0, TopLeftY = 0, Width = _w, Height = _h, MinDepth = 0, MaxDepth = 1 };
        _cmdList->RSSetViewports(1, &vpd);
        SetFullScissor();
    }

    private RECT FullScissorRect()
        => new() { left = 0, top = 0, right = (int)_w, bottom = (int)_h };

    private void SetFullScissor()
        => SetScissorRect(FullScissorRect());

    // Single scissor chokepoint with dedup: an unchanged rect records nothing (valid because RSSetScissorRects is pure
    // command-list state — only invalidated when a compositor pass sets its own scissor, via InvalidateCmdState).
    private void SetScissorRect(RECT sc)
    {
        if (_scissorValid && sc.left == _lastScissor.left && sc.top == _lastScissor.top
            && sc.right == _lastScissor.right && sc.bottom == _lastScissor.bottom) { _frameScissorSkipped++; return; }
        _lastScissor = sc;
        _scissorValid = true;
        _frameScissorSets++;
        _cmdList->RSSetScissorRects(1, &sc);
    }

    private RECT ToScissor(in RectF r)
    {
        float s = _frameScale <= 0f ? 1f : _frameScale;
        int left = (int)MathF.Floor(r.X * s);
        int top = (int)MathF.Floor(r.Y * s);
        int right = (int)MathF.Ceiling((r.X + r.W) * s);
        int bottom = (int)MathF.Ceiling((r.Y + r.H) * s);
        int maxW = (int)_w;
        int maxH = (int)_h;
        left = Math.Clamp(left, 0, maxW);
        top = Math.Clamp(top, 0, maxH);
        right = Math.Clamp(right, left, maxW);
        bottom = Math.Clamp(bottom, top, maxH);
        return new RECT { left = left, top = top, right = right, bottom = bottom };
    }

    private void SetScissor(in RectF r) => SetScissorRect(ToScissor(r));

    private static bool SameRect(in RECT a, in RECT b)
        => a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

    private RECT CurrentScissorRect()
        => _clipStack.Count == 0 ? FullScissorRect() : ToScissor(_clipStack[^1]);

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

    /// <summary>Stamp the innermost rounded clip (if any) onto a RoundRect-pipeline instance (the PS multiplies its
    /// coverage by the rounded-box SDF — the tier-2 path for animated clips on rounded surfaces).</summary>
    private void ApplyRoundedClip(ref RectInstance inst)
    {
        if (_roundedClipStack.Count == 0) return;
        var (rect, radius) = _roundedClipStack[^1];
        if (rect.W <= 0f) return;
        inst.ClipX = rect.X; inst.ClipY = rect.Y; inst.ClipW = rect.W; inst.ClipH = rect.H; inst.ClipR = radius;
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

    private int DroppedInstanceCount()
        => (_rectPipe?.DroppedInstances ?? 0) + (_shadowPipe?.DroppedInstances ?? 0) +
           (_arcPipe?.DroppedInstances ?? 0) + (_polylinePipe?.DroppedInstances ?? 0) +
           (_gradPipe?.DroppedInstances ?? 0) + (_glyphs?.DroppedInstances ?? 0) +
           (_imagePipe?.DroppedInstances ?? 0);

    private void RecordAll(float lw, float lh)
    {
        // Replay non-glyph primitives in painter (stream) order so a shadow sits OVER the background drawn before it and
        // UNDER its own element. Consecutive same-kind ops are still one batched draw. A run whose pipeline is ALREADY
        // bound on the command list (tracked in _boundPipe across segment flushes) skips the static rebind and records
        // only its SRV offset + draw — see the state-cache comment on _boundPipe. Glyphs always render last — text on
        // top within a z-context.
        if (_runs.Count > 0)
        {
            var rectSpan = CollectionsMarshal.AsSpan(_rectInsts);
            var shadowSpan = CollectionsMarshal.AsSpan(_shadowInsts);
            var arcSpan = CollectionsMarshal.AsSpan(_arcInsts);
            var polylineSpan = CollectionsMarshal.AsSpan(_polylineInsts);
            var gradSpan = CollectionsMarshal.AsSpan(_gradInsts);
            int rc = 0, sc = 0, ac = 0, pc = 0, gc = 0, ic = 0;
            _frameRuns += _runs.Count;
            foreach (var (kind, count) in _runs)
            {
                switch (kind)
                {
                    case PrimKind.Shadow:
                        bool bindShadowShared = !_sharedSdfStateBound;
                        bool bindShadowPso = _boundPipe != BoundPipe.Shadow;
                        NoteSdfPipeBind(_shadowPipe!.Record(_cmdList, shadowSpan.Slice(sc, count), lw, lh, bindShadowShared, bindShadowPso),
                            bindShadowShared, bindShadowPso, BoundPipe.Shadow);
                        sc += count; break;
                    case PrimKind.Arc:
                        bool bindArcShared = !_sharedSdfStateBound;
                        bool bindArcPso = _boundPipe != BoundPipe.Arc;
                        NoteSdfPipeBind(_arcPipe!.Record(_cmdList, arcSpan.Slice(ac, count), lw, lh, bindArcShared, bindArcPso),
                            bindArcShared, bindArcPso, BoundPipe.Arc);
                        ac += count; break;
                    case PrimKind.Polyline:
                        bool bindPolylineShared = !_sharedSdfStateBound;
                        bool bindPolylinePso = _boundPipe != BoundPipe.Polyline;
                        NoteSdfPipeBind(_polylinePipe!.Record(_cmdList, polylineSpan.Slice(pc, count), lw, lh, bindPolylineShared, bindPolylinePso),
                            bindPolylineShared, bindPolylinePso, BoundPipe.Polyline);
                        pc += count; break;
                    case PrimKind.Gradient:
                        bool bindGradientShared = !_sharedSdfStateBound;
                        bool bindGradientPso = _boundPipe != BoundPipe.Gradient;
                        NoteSdfPipeBind(_gradPipe!.Record(_cmdList, gradSpan.Slice(gc, count), lw, lh, bindGradientShared, bindGradientPso),
                            bindGradientShared, bindGradientPso, BoundPipe.Gradient);
                        gc += count; break;
                    case PrimKind.Rect:
                        bool bindRectShared = !_sharedSdfStateBound;
                        bool bindRectPso = _boundPipe != BoundPipe.Rect;
                        NoteSdfPipeBind(_rectPipe!.Record(_cmdList, rectSpan.Slice(rc, count), lw, lh, bindRectShared, bindRectPso),
                            bindRectShared, bindRectPso, BoundPipe.Rect);
                        rc += count; break;
                    case PrimKind.Image:
                        if (_boundPipe != BoundPipe.Image)
                        {
                            _imagePipe!.Begin(_cmdList, _imageTextures!.Heap, lw, lh);   // (re)bind heap/PSO/root-sig/VB for this image run
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
                }
            }
        }
        if (_glyphInsts.Count > 0)
        {
            bool rb = _boundPipe != BoundPipe.Glyph;
            NotePipeBind(_glyphs!.Record(_cmdList, _glyphInsts, lw, lh, rb), rb, BoundPipe.Glyph);
        }
        if (_gradGlyphInsts.Count > 0)   // sub-glyph wipe, same RT/z as glyphs
        {
            bool rb = _boundPipe != BoundPipe.GradGlyph;
            NotePipeBind(_glyphs!.RecordGradient(_cmdList, _gradGlyphInsts, lw, lh, rb), rb, BoundPipe.GradGlyph);
        }
    }

    private void FlushSegment(float lw, float lh)
    {
        if (!HasPendingSegment()) return;
        _frameSegments++;
        if (_desiredScissorValid) SetScissorRect(_desiredScissor);
        else ResetDesiredScissor();
        _glyphs!.UploadIfDirty(_cmdList);
        RecordAll(lw, lh);
        ClearInsts();
    }

    private void SubmitStreaming(ReadOnlySpan<byte> drawList, float lw, float lh)
    {
        ClearInsts();
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
            pos = DecodeOne(drawList, pos);
        }
        FlushSegment(lw, lh);
        _clipStack.Clear();
        _roundedClipStack.Clear();
        _desiredScissor = FullScissorRect();
        _desiredScissorValid = true;
        SetFullScissor();
    }

    // Layered path: render the scene into the canvas, processing the stream in order. An Acrylic PushLayer blurs the
    // backdrop drawn so far then composites the frosted surface (content draws on top; its PopLayer is a no-op). An
    // Opacity PushLayer redirects drawing into a leased transparent canvas-sized RT; its PopLayer composites that RT
    // once over the underlying target at GroupAlpha (flat group — no double-blend). Kinds nest via _layerKinds.
    private void SubmitWithLayers(ReadOnlySpan<byte> drawList, in FrameInfo ctx, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE backRtv, bool directToBackBuffer)
    {
        // completed fence gates pool retire/drain; (frameIndex & 1) selects this frame's parity-banked SRV slots.
        ulong completed = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (directToBackBuffer)
        {
            // Composite all layer groups straight onto the back buffer. Blur/opacity/edge-fade blend OVER it (the blur
            // samples its own offscreen group RT); an ACRYLIC snapshots only its small back-buffer region into the canvas
            // (BlurAndComposite below). Bind + clear the back buffer like the streaming path — the full-window scene→canvas
            // render + blit are skipped. BeginFrameDirect sets the acrylic pool's SRV parity bank + runs upkeep WITHOUT the
            // full-window canvas clear, so a stationary acrylic's retained-backdrop cache stays parity-correct.
            _acrylic!.BeginFrameDirect(completed, (int)(_frameIndex & 1));
            _cmdList->OMSetRenderTargets(1, &backRtv, BOOL.FALSE, null);
            float* clr = stackalloc float[4] { ctx.Clear.R, ctx.Clear.G, ctx.Clear.B, ctx.Clear.A };
            _cmdList->ClearRenderTargetView(backRtv, clr, 0, null);
            SetFullViewport();
        }
        else _acrylic!.BeginCanvas(_cmdList, ctx.Clear, completed, (int)(_frameIndex & 1));
        _opacity!.BeginFrame(completed, (int)(_frameIndex & 1));
        InvalidateCmdState();   // canvas/back-buffer setup may have touched viewport/scissor outside the cache
        ClearInsts();
        _clipStack.Clear();
        _roundedClipStack.Clear();
        ResetDesiredScissor();
        _layerKinds.Clear();
        _opacityGroups.Clear();
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
            if (op == DrawOp.PushLayer)
            {
                int p2 = pos + sizeof(int);
                var L = MemoryMarshal.Read<PushLayerCmd>(drawList.Slice(p2));
                pos = p2 + Unsafe.SizeOf<PushLayerCmd>();
                _frameLayerOps++;
                FlushSegment(lw, lh);                       // draw the backdrop-so-far into the current target
                if (L.Kind == (int)LayerKind.Opacity || L.Kind == (int)LayerKind.Blur || L.Kind == (int)LayerKind.EdgeFade)
                {
                    // Blur cache: a self-blur whose subtree bytes + σ are unchanged reuses last frame's retained blurred
                    // RT (skip the render + both Gaussian passes). Safe-bails to the normal lease path on a nested layer
                    // or an unrecognized op. `pos` is already past the PushLayerCmd, i.e. at the first subtree op.
                    bool holdIfCached = L.Kind == (int)LayerKind.Blur && L.BlurCachePolicy != (int)BlurCachePolicy.Normal;
                    bool skipOnHoldMiss = L.BlurCachePolicy == (int)BlurCachePolicy.HoldOrSkipOnMiss;
                    if (L.Kind == (int)LayerKind.Blur && L.BlurSigma > 0f
                        && BlurPinKey.TryCompute(drawList, pos, in L, out ulong bhash, out int afterPop))
                    {
                        if (_curBlurHashCount < _curBlurHashes.Length) _curBlurHashes[_curBlurHashCount++] = bhash;
                        int pin = _opacity.FindPin(bhash, _fenceValue + 1, in L, _frameScale);   // G1: a hit refreshes recency (MRU); size-exact match
                        // An edge-clamped (partial) region can't reuse a full-strip pin without vertically squishing it,
                        // so a clamped self-blur is uncacheable — miss (render + blur at the exact clamped region) here.
                        bool regionClamped = _opacity.RegionIsClamped(in L, _frameScale);
                        // Position-independent key ⇒ a scrolled (translated) strip HITS its pin. On the SETTLE frame
                        // (InMotion==0) where the pin was captured at a different integer origin, fall through to one exact
                        // re-blur at rest (settle exactness) instead of compositing the mid-motion pin.
                        if (pin >= 0 && !regionClamped && !(L.InMotion == 0 && _opacity.PinOriginDiffers(pin, in L, _frameScale)))
                        {
                            // HIT: composite the cached blur over the enclosing target; skip the subtree + its PopLayer.
                            if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);
                            else BindLayerTopTarget(directToBackBuffer, backRtv);
                            _opacity.CompositePinnedBlur(_cmdList, pin, L.GroupAlpha, in L, _frameScale, CurrentScissorRect());
                            InvalidateCmdState();   // the composite bound its own PSO/heap + viewport/scissor
                            ApplyCurrentScissor();
                            _blurCacheHit++;
                            if (holdIfCached) _blurHoldHit++;
                            pos = afterPop;
                            continue;
                        }
                        // MISS: render into a NORMAL transient scratch (unchanged path), and TAG the group with bhash so
                        // PopLayer copies the blurred region into a small retained pin AFTER the normal composite — but only
                        // if this exact content recurred from last frame ⇒ STATIONARY ⇒ likely a hit next frame. A fresh hash
                        // (scroll/animation moves content every frame) leaves pinHash 0 below ⇒ pure transient, no pin churn.
                        if (BlurHashSeenLastFrame(bhash))
                        {
                            int pslot = _opacity.Acquire(_cmdList, _fenceValue + 1);
                            // Don't pin an edge-clamped (partial) capture: it holds only the on-canvas slice, so a later
                            // frame at a different clamp would squish it, and each clamp size would mint a distinct
                            // duplicate same-hash pin, thrashing the budget. pinHash 0 ⇒ pure transient (re-blur next frame).
                            ulong pinTag = regionClamped ? 0UL : bhash;
                            _opacityGroups.Add((pslot, L, pinTag));
                            _layerKinds.Add(L.Kind);
                            _blurCacheMiss++;
                            continue;
                        }
                        if (holdIfCached)
                        {
                            if (skipOnHoldMiss) pos = afterPop;
                            else _layerKinds.Add(NoopLayerKind);
                            _blurHoldFallback++;
                            continue;
                        }
                    }
                    else if (holdIfCached && L.BlurSigma > 0f)
                    {
                        // Uncacheable (TryCompute bailed on a nested layer / unknown op) under a hold policy. HoldOrSkipOnMiss
                        // must still SKIP the subtree (drop it, exactly like the cacheable hold-miss path above) rather than
                        // flash it crisp; without a hash we don't have afterPop, so walk the subtree to its matching PopLayer.
                        if (skipOnHoldMiss && TrySkipLayerSubtree(drawList, pos, out int skipTo)) pos = skipTo;
                        else _layerKinds.Add(NoopLayerKind);   // HoldIfCached (or an unwalkable subtree): crisp inline fallback
                        _blurHoldFallback++;
                        continue;
                    }
                    // Lease + clear + bind the group RT; scissor state carries over so the subtree clips identically. A
                    // Blur (or edge-fade-with-blur) group carries its σ — the RT is gaussian-blurred on pop before the
                    // composite; an edge-fade carries the per-edge bands + corners, applied in EdgeFadeComposite on pop.
                    int slot = _opacity.Acquire(_cmdList, _fenceValue + 1);
                    _opacityGroups.Add((slot, L, 0UL));
                    _layerKinds.Add(L.Kind);
                }
                else
                {
                    // region snapshot → pooled-RT separable blur → acrylic composite (re-binds canvas + full viewport).
                    // _fenceValue + 1 is the fence value SignalFrame will signal for THIS frame (gates pooled-RT retire).
                    // Documented limitation: nested inside an open opacity group, the acrylic composites into the
                    // CANVAS (the acrylic leaf owns its canvas binding) — combine acrylic + group on one node instead.
                    // ctx.Damage is the DIP union of nodes that moved this frame → physical px for the cache's region test.
                    _acrylic.BlurAndComposite(_cmdList, L, lw, lh, _frameScale, _fenceValue + 1,
                        ctx.Damage.X * _frameScale, ctx.Damage.Y * _frameScale, ctx.Damage.W * _frameScale, ctx.Damage.H * _frameScale,
                        directToBackBuffer ? _backBuffers[_frameIndex] : null, directToBackBuffer ? backRtv : default);
                    InvalidateCmdState();   // the acrylic passes bound their own PSOs/heap + viewport/scissor
                    if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);   // back to the open group RT
                    ApplyCurrentScissor();
                    _layerKinds.Add((int)LayerKind.Acrylic);
                }
                continue;
            }
            if (op == DrawOp.PopLayer)
            {
                pos += sizeof(int) + Unsafe.SizeOf<PopLayerCmd>();
                _frameLayerOps++;
                int kind = _layerKinds.Count > 0 ? _layerKinds[^1] : (int)LayerKind.Acrylic;
                if (_layerKinds.Count > 0) _layerKinds.RemoveAt(_layerKinds.Count - 1);
                if ((kind == (int)LayerKind.Opacity || kind == (int)LayerKind.Blur || kind == (int)LayerKind.EdgeFade) && _opacityGroups.Count > 0)
                {
                    FlushSegment(lw, lh);                   // finish the subtree into the group RT
                    var (slot, gl, pinHash) = _opacityGroups[^1];
                    _opacityGroups.RemoveAt(_opacityGroups.Count - 1);
                    // Self-blur (or edge-fade-with-blur): gaussian-blur the group RT in place (leaves it readable); else BeginRead.
                    if ((kind == (int)LayerKind.Blur || kind == (int)LayerKind.EdgeFade) && gl.BlurSigma > 0f)
                        _opacity.BlurInPlace(_cmdList, slot, gl.BlurSigma, _fenceValue + 1, in gl, _frameScale);
                    else _opacity.BeginRead(_cmdList, slot);
                    InvalidateCmdState();   // BlurInPlace set its own scissor/PSO — MUST invalidate before BindLayerTopTarget's SetFullViewport dedup
                    // Composite over the UNDERLYING target: the enclosing group's RT, or the top-level target (canvas, or
                    // the back buffer directly on the FG_BACKBUFFER_LAYERS path).
                    if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);
                    else BindLayerTopTarget(directToBackBuffer, backRtv);
                    ApplyCurrentScissor();
                    if (kind == (int)LayerKind.EdgeFade) _opacity.EdgeFadeComposite(_cmdList, slot, in gl, _frameScale, CurrentScissorRect());
                    else _opacity.Composite(_cmdList, slot, gl.GroupAlpha);
                    // Blur-cache MISS (pinHash != 0): COPY the just-composited blurred region out of the scratch into a small
                    // retained pin for next frame's FindPin. The scratch is ALWAYS released (the pin is a separate small RT).
                    if (pinHash != 0) _opacity.RetainPinFromScratch(_cmdList, slot, pinHash, in gl, _frameScale, _fenceValue + 1);
                    _opacity.Release(slot);
                    InvalidateCmdState();   // BlurInPlace/Composite/EdgeFadeComposite bound their own PSOs/heap + scissor
                    ApplyCurrentScissor();
                }
                continue;
            }
            pos = DecodeOne(drawList, pos);
        }
        FlushSegment(lw, lh);
        // Defensive: a malformed stream that left groups open still composites them (full alpha chain preserved).
        while (_opacityGroups.Count > 0)
        {
            var (slot, gl, pinHash) = _opacityGroups[^1];
            _opacityGroups.RemoveAt(_opacityGroups.Count - 1);
            if (gl.BlurSigma > 0f) _opacity.BlurInPlace(_cmdList, slot, gl.BlurSigma, _fenceValue + 1, in gl, _frameScale);
            else _opacity.BeginRead(_cmdList, slot);
            InvalidateCmdState();   // as in the main PopLayer branch: BlurInPlace bypassed the scissor cache
            if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);
            else BindLayerTopTarget(directToBackBuffer, backRtv);
            ApplyCurrentScissor();
            if (gl.Kind == (int)LayerKind.EdgeFade) _opacity.EdgeFadeComposite(_cmdList, slot, in gl, _frameScale, CurrentScissorRect());
            else _opacity.Composite(_cmdList, slot, gl.GroupAlpha);
            if (pinHash != 0) _opacity.RetainPinFromScratch(_cmdList, slot, pinHash, in gl, _frameScale, _fenceValue + 1);
            _opacity.Release(slot);
        }
        _layerKinds.Clear();
        _clipStack.Clear();
        _roundedClipStack.Clear();
        if (!directToBackBuffer) _acrylic.BlitToBackBuffer(_cmdList, backRtv);   // back-buffer-direct path already drew there
    }

    // Bind the TOP-LEVEL composite target for the layered path — the back buffer (FG_BACKBUFFER_LAYERS fast path) or the
    // engine canvas (acrylic path). The caller follows with ApplyCurrentScissor to restore the enclosing clip.
    private void BindLayerTopTarget(bool directToBackBuffer, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        if (directToBackBuffer) { _cmdList->OMSetRenderTargets(1, &backRtv, BOOL.FALSE, null); SetFullViewport(); }
        else _acrylic!.BindCanvas(_cmdList);
    }

    // The self-blur pin cache's position-independent content key is computed by FluentGpu.Render.BlurPinKey.TryCompute
    // (portable, so the headless VerticalSlice can gate it) — it folds σ + integer device size + the subtree op bytes with
    // each op's position REBASED to the layer origin, so a pure scroll/translation reuses the pin (see backdrop-effects-
    // animation.md §FA-2a). It replaced the old absolute-DeviceRect hash that missed on every position change.

    // Did this blur content hash appear LAST frame? (recurrence ⇒ stationary content worth pinning; see the ring fields.)
    private bool BlurHashSeenLastFrame(ulong hash)
    {
        for (int i = 0; i < _lastBlurHashCount; i++) if (_lastBlurHashes[i] == hash) return true;
        return false;
    }

    // Classify the primary stream's layers in ONE pass: 0 = no layers, 1 = opacity/blur/edge-fade layers only, 2 = an
    // ACRYLIC layer is present. Only acrylic must snapshot the backdrop (→ needs the offscreen canvas); kind 1 can render
    // straight to the back buffer (FG_BACKBUFFER_LAYERS). Mirrors the old StreamHasLayer decode, reading PushLayerCmd.Kind.
    private static int StreamLayerKind(ReadOnlySpan<byte> cmds)
    {
        int pos = 0, kind = 0;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect: pos += Unsafe.SizeOf<FillRoundRectCmd>(); break;
                case DrawOp.DrawGlyphRun: pos += Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
                case DrawOp.DrawGlyphRunGradient: pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break;
                case DrawOp.PushClip: pos += Unsafe.SizeOf<ClipCmd>(); break;
                case DrawOp.PopClip: break;
                case DrawOp.DrawImage: pos += Unsafe.SizeOf<DrawImageCmd>(); break;
                case DrawOp.DrawRoundRectStroke: pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break;
                case DrawOp.DrawShadow: pos += Unsafe.SizeOf<DrawShadowCmd>(); break;
                case DrawOp.DrawArc: pos += Unsafe.SizeOf<DrawArcCmd>(); break;
                case DrawOp.DrawPolylineStroke: pos += Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break;
                case DrawOp.DrawGradientRect: pos += Unsafe.SizeOf<DrawGradientRectCmd>(); break;
                case DrawOp.DrawGradientStroke: pos += Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
                case DrawOp.DrawTabShape: pos += Unsafe.SizeOf<DrawTabShapeCmd>(); break;
                case DrawOp.PushLayer:
                    var L = MemoryMarshal.Read<PushLayerCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<PushLayerCmd>();
                    if (L.Kind == (int)LayerKind.Acrylic) return 2;   // acrylic ⇒ canvas path required (decided)
                    kind = 1;
                    break;
                case DrawOp.PopLayer: pos += Unsafe.SizeOf<PopLayerCmd>(); break;
                default: return kind;
            }
        }
        return kind;
    }

    // Byte offset just past the PopLayer matching the open layer whose subtree begins at `start` (the first op after the
    // PushLayerCmd, already consumed), counting nested layer depth. Used to SKIP an uncacheable HoldOrSkipOnMiss self-blur
    // when no BlurPinKey hash (⇒ no afterPop) is available. Returns false on an op this decoder can't size — mirrors the
    // StreamLayerKind size table — so the caller keeps its crisp fallback instead of desyncing the stream.
    private static bool TrySkipLayerSubtree(ReadOnlySpan<byte> cmds, int start, out int afterPop)
    {
        afterPop = start;
        int pos = start, depth = 0;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect: pos += Unsafe.SizeOf<FillRoundRectCmd>(); break;
                case DrawOp.DrawGlyphRun: pos += Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
                case DrawOp.DrawGlyphRunGradient: pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break;
                case DrawOp.PushClip: pos += Unsafe.SizeOf<ClipCmd>(); break;
                case DrawOp.PopClip: break;
                case DrawOp.DrawImage: pos += Unsafe.SizeOf<DrawImageCmd>(); break;
                case DrawOp.DrawRoundRectStroke: pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break;
                case DrawOp.DrawShadow: pos += Unsafe.SizeOf<DrawShadowCmd>(); break;
                case DrawOp.DrawArc: pos += Unsafe.SizeOf<DrawArcCmd>(); break;
                case DrawOp.DrawPolylineStroke: pos += Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break;
                case DrawOp.DrawGradientRect: pos += Unsafe.SizeOf<DrawGradientRectCmd>(); break;
                case DrawOp.DrawGradientStroke: pos += Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
                case DrawOp.DrawTabShape: pos += Unsafe.SizeOf<DrawTabShapeCmd>(); break;
                case DrawOp.PushLayer: pos += Unsafe.SizeOf<PushLayerCmd>(); depth++; break;
                case DrawOp.PopLayer:
                    pos += Unsafe.SizeOf<PopLayerCmd>();
                    if (depth == 0) { afterPop = pos; return true; }
                    depth--;
                    break;
                default: return false;   // an op this decoder can't size — keep the crisp fallback
            }
        }
        return false;
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
    }

    internal bool Vsync { get => _vsync; set => _vsync = value; }

    internal void Present(D3D12Swapchain target)
    {
        if (target.Disposed) return;
        AssertSubmitThread();   // seam Step 0: when render-confined (force-sync/async), only the render thread may present
        Activate(target);
        // Seam (DIM on-screen fix): bind the DirectComposition graph on the PRESENTING thread the first time this composited
        // swapchain presents — deferred out of the UI-thread InitSwapChain so DComp is owned by the thread that Presents.
        if (target.Composited && target.DcompBindPending) BindDComp(target);
        // A keep-alive repaint fired from inside an OS modal move/size loop (host called SuppressVsyncOnce) presents at
        // SyncInterval 0 so the WndProc thread isn't blocked up to a vblank — the live-resize/move hitch. On the composited
        // DComp flip swapchain interval-0 is a cheap, tear-free hand-off (DWM still composites at vblank); steady-state
        // frames keep _vsync's interval. Self-resetting: one unsynced present per call.
        bool noVsync = _skipVsyncOnce; _skipVsyncOnce = false;
        // ALLOW_TEARING is valid only with sync-interval 0 on a tearing-capable (non-composited) swapchain; on the
        // composition path _tearingSupported is false so the modal-tick present is interval 0 with flags 0 (valid + tear-free).
        uint interval = (_vsync && !noVsync) ? 1u : 0u;
        uint flags = (interval == 0 && _tearingSupported) ? DXGI.DXGI_PRESENT_ALLOW_TEARING : 0u;
        HRESULT pr = (HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChainVtbl.Present(_swapChain, interval, flags);   // GEN-COM (wired)
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
        StoreActive();
    }

    private void SignalFrame(uint frameIndex)
    {
        ulong v = ++_fenceValue;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.Signal(_queue, _fence, v), "queue.Signal");   // GEN-COM (wired)
        _frameFenceValues[frameIndex] = v;
    }

    private void WaitForFrame(uint frameIndex)
    {
        ulong v = _frameFenceValues[frameIndex];
        if (v == 0 || global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence) >= v) return;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.SetEventOnCompletion(_fence, v, (void*)_fenceEvent), "SetEventOnCompletion");   // GEN-COM (wired)
        if (_signalDeviceLostInsteadOfThrow) WaitFenceEventBounded();   // Step 4: no INFINITE hang on a lost device (async)
        else WaitForSingleObject(_fenceEvent, INFINITE);
    }

    // Block until the swapchain is ready to accept a new frame (bounds present-queue depth → lower latency, efficient wait).
    // Bounded timeout so a lost device can't hang the loop. No-op if the waitable wasn't created (older DXGI / failure).
    private void WaitForLatency()
    {
        if (_hasLatencyWaitable) WaitForSingleObject(_frameLatencyWaitable, 1000);
    }

    private bool _skipLatencyOnce;   // set by SuppressLatencyWaitOnce, consumed by the next SubmitDrawList

    private double _lastFenceWaitMs;   // wall-time blocked on the latency waitable + frame fence in the last SubmitDrawList
    /// <inheritdoc/>
    public double LastFenceWaitMs => _lastFenceWaitMs;

    /// <inheritdoc/>
    public bool HasPendingUploads => _imageTextures?.HasPendingUploads ?? false;

    public void SuppressLatencyWaitOnce() => _skipLatencyOnce = true;

    private bool _skipVsyncOnce;     // set by SuppressVsyncOnce, consumed by the next Present (interval 0 — non-blocking modal-loop tick)

    public void SuppressVsyncOnce() => _skipVsyncOnce = true;

    private bool CheckTearingSupport()
    {
        IDXGIFactory5* f5;
        if ((int)_factory->QueryInterface(__uuidof<IDXGIFactory5>(), (void**)&f5) < 0) return false;
        int allow = 0;
        HRESULT hr = f5->CheckFeatureSupport(DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow, sizeof(int));
        f5->Release();
        return (int)hr >= 0 && allow != 0;
    }

    internal void WaitForGpu()
    {
        ulong v = ++_fenceValue;
        Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12CommandQueueVtbl.Signal(_queue, _fence, v), "queue.Signal");   // GEN-COM (wired)
        if (global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence) < v)
        {
            Check((HRESULT)global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.SetEventOnCompletion(_fence, v, (void*)_fenceEvent), "SetEventOnCompletion");   // GEN-COM (wired)
            if (_signalDeviceLostInsteadOfThrow) WaitFenceEventBounded();   // Step 4: no INFINITE hang on a lost device (async)
            else WaitForSingleObject(_fenceEvent, INFINITE);
        }
    }

    /// <summary>
    /// Debug-only: read the last-rendered back buffer back to CPU as tightly-packed, top-down BGRA8. Used by the
    /// <c>--screenshot</c> tooling to produce a PNG for visual fidelity diffing — NOT a hot-path method (it stalls
    /// the GPU). With FLIP_DISCARD the just-presented buffer at <c>_frameIndex</c> is still intact until reused.
    /// </summary>
    public byte[] CaptureBgra(out int width, out int height)
    {
        if (_primarySwapchain is null) throw new InvalidOperationException("CreateSwapchain must be called before CaptureBgra.");
        Activate(_primarySwapchain);
        WaitForGpu();   // ensure the last frame finished rendering before we copy it
        width = (int)_w; height = (int)_h;
        ID3D12Resource* back = _backBuffers[_frameIndex];

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

        ID3D12CommandAllocator* alloc = _allocators[_frameIndex];
        Check(alloc->Reset(), "capture.alloc.Reset");
        Check(_cmdList->Reset(alloc, null), "capture.cmd.Reset");
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
        return outp;
    }

    internal void Resize(D3D12Swapchain target, uint w, uint h)
    {
        if (w < 1) w = 1; if (h < 1) h = 1;
        if (target.Disposed || (w == target.W && h == target.H)) return;
        Activate(target);
        WaitForGpu();
        D3D12MemoryDiagnostics.Resize("Swapchain", w, h);
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            if (target.BackBuffers[i] != null)
            {
                D3D12MemoryDiagnostics.Release(target.BackBuffers[i], $"Swapchain.BackBuffer[{i}]");
                target.BackBuffers[i]->Release();
                target.BackBuffers[i] = null;
                _backBuffers[i] = null;
            }
        }
        Check(target.SwapChain->ResizeBuffers(FRAME_COUNT, w, h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, target.SwapChainFlags), "ResizeBuffers");
        target.W = w; target.H = h;
        _w = w; _h = h;
        CreateRtvs(target);
        target.FrameIndex = target.SwapChain->GetCurrentBackBufferIndex();
        target.Backdrop?.SetBounds(w, h);   // resize the WUC backdrop/content visuals to match
        Activate(target);
    }

    public Size2 SizePx => _primarySwapchain is { } sc ? sc.SizePx : new(_w, _h);

    internal void DisposeSwapchain(D3D12Swapchain target)
    {
        if (target.Disposed) return;
        if (_device != null) WaitForGpu();
        ReleaseSwapchainResources(target);
        _swapchains.Remove(target);
        if (_primarySwapchain == target) _primarySwapchain = null;
        if (_activeSwapchain == target) _activeSwapchain = null;
    }

    private void ReleaseSwapchainResources(D3D12Swapchain target)
    {
        if (target.Disposed) return;
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
        if (target.DcompTarget != null) { target.DcompTarget->Release(); target.DcompTarget = null; }
        if (target.SwapChain != null) { target.SwapChain->Release(); target.SwapChain = null; }
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
        //    survive for re-init); reset Disposed since we are recreating, not tearing down.
        for (int i = 0; i < _swapchains.Count; i++)
        {
            ReleaseSwapchainResources(_swapchains[i]);
            _swapchains[i].Disposed = false;
        }
        for (uint i = 0; i < FRAME_COUNT; i++) _backBuffers[i] = null;
        // 2. Release device-level ComPtrs + pipelines (null the pipe fields so EnsurePipelines re-runs). NO WaitForGpu.
        if (_dcomp != null) { _dcomp->Release(); _dcomp = null; }
        _glyphs?.Dispose(); _glyphs = null;
        _imagePipe?.Dispose(); _imagePipe = null;
        _imageTextures?.Dispose(); _imageTextures = null;
        _shadowPipe?.Dispose(); _shadowPipe = null;
        _arcPipe?.Dispose(); _arcPipe = null;
        _polylinePipe?.Dispose(); _polylinePipe = null;
        _gradPipe?.Dispose(); _gradPipe = null;
        _acrylic?.Dispose(); _acrylic = null;
        _opacity?.Dispose(); _opacity = null;
        _rectPipe?.Dispose(); _rectPipe = null;
        _sdf?.Dispose(); _sdf = null;
        if (_cmdList != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_cmdList); _cmdList = null; }
        for (uint i = 0; i < FRAME_COUNT; i++)
            if (_allocators[i] != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_allocators[i]); _allocators[i] = null; }
        if (_fence != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_fence); _fence = null; }
        if (_queue != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_queue); _queue = null; }
        if (_factory != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_factory); _factory = null; }
        if (_device != null) { global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_device); _device = null; }
        if (_fenceEvent != HANDLE.NULL) { CloseHandle(_fenceEvent); _fenceEvent = HANDLE.NULL; }

        // 3. Recreate the device/queue/allocators/command-list/fence/event (InitDevice resets _fenceValue = 0).
        InitDevice();
        // 4. Zero the per-back-buffer fence bookkeeping so WaitForFrame's v==0 early-out fires for the first FRAME_COUNT
        //    frames — the fresh fence never reaches the stale pre-loss targets.
        System.Array.Clear(_frameFenceValues, 0, _frameFenceValues.Length);
        // 5. Recreate all pipelines + a fresh (empty) image store; re-arm async image confinement on the new store.
        EnsurePipelines();
        if (_signalDeviceLostInsteadOfThrow) _imageTextures?.MarkRenderConfined();
        // 6. Recreate every swapchain (SwapChain / RtvHeap / BackBuffers / Backdrop) at its retained size, then rebind its
        //    DirectComposition graph HERE (RecoverDevice is render-confined, AssertSubmitThread at the top) so the recover
        //    frame is composited-correct on the render thread without waiting for the next Present's lazy bind.
        for (int i = 0; i < _swapchains.Count; i++)
        {
            InitSwapChain(_swapchains[i]);
            if (_swapchains[i].Composited) BindDComp(_swapchains[i]);
        }
        if (_primarySwapchain != null) Activate(_primarySwapchain);
        // 7. Healthy again.
        System.Threading.Volatile.Write(ref _deviceLostReason, 0);
    }

    public void Dispose()
    {
        if (_device != null) WaitForGpu();
        for (int i = _swapchains.Count - 1; i >= 0; i--)
            ReleaseSwapchainResources(_swapchains[i]);
        _swapchains.Clear();
        _primarySwapchain = null;
        _activeSwapchain = null;
        if (_dcomp != null) _dcomp->Release();
        _glyphs?.Dispose();
        _imagePipe?.Dispose();
        _imageTextures?.Dispose();
        _shadowPipe?.Dispose();
        _arcPipe?.Dispose();
        _polylinePipe?.Dispose();
        _gradPipe?.Dispose();
        _acrylic?.Dispose();
        _opacity?.Dispose();
        _rectPipe?.Dispose();
        _sdf?.Dispose();
        for (uint i = 0; i < FRAME_COUNT; i++) _backBuffers[i] = null;
        D3D12MemoryDiagnostics.Snapshot("D3D12Device.Dispose");
        // GEN-COM (wired): COM teardown via the generated IUnknown.Release calli (vtable slot 2, universal to every COM ptr).
        if (_cmdList != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_cmdList);
        for (uint i = 0; i < FRAME_COUNT; i++)
            if (_allocators[i] != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_allocators[i]);
        if (_fence != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_fence);
        if (_queue != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_queue);
        if (_factory != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_factory);
        if (_device != null) global::FluentGpu.Interop.Generated.IUnknownVtbl.Release(_device);
        if (_fenceEvent != HANDLE.NULL) CloseHandle(_fenceEvent);
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
    internal uint SwapChainFlags;
    internal bool TearingSupported;
    internal uint W, H, FrameIndex;
    internal IDCompositionTarget* DcompTarget;
    internal IDCompositionVisual* DcompVisual;
    internal bool DcompBindPending;   // seam: DComp graph deferred out of UI-thread InitSwapChain; bound on the presenting thread (see BindDComp)
    internal CompositionBackdrop? Backdrop;   // non-null ⇒ WUC desktop-acrylic popup (replaces the DComp path)
    internal readonly bool DesktopAcrylic;
    internal readonly ColorF AcrylicTint;
    internal readonly float CornerRadiusPx;
    internal bool Disposed;

    internal D3D12Swapchain(D3D12Device device, HWND hwnd, uint w, uint h, bool composited, bool desktopAcrylic, ColorF acrylicTint, float cornerRadiusPx)
    {
        Device = device;
        Hwnd = hwnd;
        W = w;
        H = h;
        Composited = composited;
        DesktopAcrylic = desktopAcrylic;
        AcrylicTint = acrylicTint;
        CornerRadiusPx = cornerRadiusPx;
    }

    public Size2 SizePx => new(W, H);
    public void Resize(Size2 px) => Device.Resize(this, (uint)px.Width, (uint)px.Height);
    public void Present() => Device.Present(this);
    public void ConfigurePopupChrome(in PopupChromeMetrics m) => Backdrop?.ConfigureChrome(m.ContentRectPx, m.OpensUp, m.ClosedRatio, m.CornerRadiusPx);
    public void AnimatePopupOpen() => Backdrop?.AnimateOpen();
    public void AnimatePopupClose() => Backdrop?.AnimateClose();
    public bool PopupAnimating => Backdrop?.IsAnimating ?? false;
    public void Dispose() => Device.DisposeSwapchain(this);
}
