using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
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
    private const uint FRAME_COUNT = 2;
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
    private bool _vsync = true;             // Present sync-interval 1 when true; interval 0 + ALLOW_TEARING when false

    private HWND _hwnd;
    private uint _w, _h;
    private uint _frameIndex;
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
    // Open PushLayer kinds in stream order (acrylic pops are no-ops; opacity + self-blur pops composite their leased
    // RT), and the leased (slot, alpha, σ) per open OPACITY/BLUR group — both reused across frames (0 steady alloc). A
    // blur group is an opacity group with Sigma > 0: its RT is separable-Gaussian-blurred before the flat composite.
    private readonly List<int> _layerKinds = new(8);
    private readonly List<(int Slot, float Alpha, float Sigma)> _opacityGroups = new(4);
    private GlyphRenderer? _glyphs;
    private ImageTextureStore? _imageTextures;
    private ImagePipeline? _imagePipe;
    private readonly List<(ImageInstance inst, int imageId)> _imageDraws = new();
    // Painter-order draw runs: a segment's non-glyph primitives in STREAM order (consecutive same-kind ops batched into
    // one draw), so a shadow correctly sits OVER the background drawn before it and UNDER the element it belongs to.
    // Without this, all shadows batch before all rects and any opaque background paints over them. Glyphs always draw last.
    private enum PrimKind : byte { Rect, Shadow, Gradient, Image, Arc, Polyline }
    private readonly List<(PrimKind Kind, int Count)> _runs = new();
    private int _frameImageCount;
    private int _frameImageSkipped;
    private readonly List<GlyphInstance> _glyphInsts = new();
    private float _frameScale = 1f;
    private int _frameRectCount;
    private int _frameGlyphInstanceCount;
    private readonly StringTable _strings;
    private readonly bool _composited;

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
              $" | tex: atlas={_imageTextures.AtlasImageCount} pooledFree={_imageTextures.PooledTextureCount} retired={_imageTextures.RetiredCount}";

    internal ID3D12Device* Device => _device;
    internal ID3D12GraphicsCommandList* CommandList => _cmdList;

    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        _hwnd = (HWND)desc.PresentTarget.Value;
        _w = (uint)Math.Max(1, (int)desc.SizePx.Width);
        _h = (uint)Math.Max(1, (int)desc.SizePx.Height);
        InitDevice();
        InitSwapChain();
        _rectPipe = new RoundRectPipeline();
        _rectPipe.Init(_device);
        _shadowPipe = new ShadowPipeline();
        _shadowPipe.Init(_device);
        _arcPipe = new ArcPipeline();
        _arcPipe.Init(_device);
        _polylinePipe = new PolylineStrokePipeline();
        _polylinePipe.Init(_device);
        _gradPipe = new GradientPipeline();
        _gradPipe.Init(_device);
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
        return new D3D12Swapchain(this);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
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

        D3D12_COMMAND_QUEUE_DESC qd = default;
        qd.Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT;
        qd.Flags = D3D12_COMMAND_QUEUE_FLAGS.D3D12_COMMAND_QUEUE_FLAG_NONE;
        ID3D12CommandQueue* queue;
        Check(_device->CreateCommandQueue(&qd, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
        _queue = queue;

        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            ID3D12CommandAllocator* alloc;
            Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator");
            _allocators[i] = alloc;
        }

        ID3D12GraphicsCommandList* list;
        Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, _allocators[0], null,
            __uuidof<ID3D12GraphicsCommandList>(), (void**)&list), "CreateCommandList");
        _cmdList = list;
        _cmdList->Close();   // start closed; opened each frame after Reset

        ID3D12Fence* fence;
        Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence), "CreateFence");
        _fence = fence;
        _fenceValue = 0;
        _fenceEvent = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);

        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hd.NumDescriptors = FRAME_COUNT;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        ID3D12DescriptorHeap* heap;
        Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap");
        _rtvHeap = heap;
        _rtvSize = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12MemoryDiagnostics.Track(_rtvHeap, "Device.RtvHeap", (ulong)FRAME_COUNT * _rtvSize);
    }

    private void InitSwapChain()
    {
        DXGI_SWAP_CHAIN_DESC1 sd = default;
        sd.Width = _w;
        sd.Height = _h;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.Stereo = BOOL.FALSE;
        sd.SampleDesc.Count = 1;
        sd.SampleDesc.Quality = 0;
        sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = FRAME_COUNT;
        sd.Scaling = _composited ? DXGI_SCALING.DXGI_SCALING_STRETCH : DXGI_SCALING.DXGI_SCALING_STRETCH;
        sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
        sd.AlphaMode = _composited ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;

        // Latency-waitable swapchain (canon: pal-rhi.md §5.1 / budgets.md) — lets us bound queued frames and wait efficiently
        // for present-readiness instead of blocking deep on the GPU fence. ALLOW_TEARING (hwnd + vsync-off only) needs both the
        // swapchain flag here AND the matching present flag, gated by factory support.
        _tearingSupported = !_composited && CheckTearingSupport();
        uint flags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        if (_tearingSupported) flags |= (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
        _swapChainFlags = flags;
        sd.Flags = flags;

        IDXGISwapChain1* sc1;
        if (_composited)
            Check(_factory->CreateSwapChainForComposition((IUnknown*)_queue, &sd, null, &sc1), "CreateSwapChainForComposition");
        else
            Check(_factory->CreateSwapChainForHwnd((IUnknown*)_queue, _hwnd, &sd, null, null, &sc1), "CreateSwapChainForHwnd");

        IDXGISwapChain3* sc3;
        Check(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3");
        sc1->Release();
        _swapChain = sc3;

        // IDXGISwapChain3 : IDXGISwapChain2 — cap the queued frames and grab the latency waitable (created above via the flag).
        Check(_swapChain->SetMaximumFrameLatency(FRAME_COUNT - 1), "SetMaximumFrameLatency");
        _frameLatencyWaitable = _swapChain->GetFrameLatencyWaitableObject();
        _hasLatencyWaitable = _frameLatencyWaitable != HANDLE.NULL;

        if (_composited)
        {
            IDCompositionDevice* dc;
            Check(DCompositionCreateDevice(null, __uuidof<IDCompositionDevice>(), (void**)&dc), "DCompositionCreateDevice");
            _dcomp = dc;
            IDCompositionTarget* target;
            Check(_dcomp->CreateTargetForHwnd(_hwnd, BOOL.TRUE, &target), "CreateTargetForHwnd");
            _dcompTarget = target;
            IDCompositionVisual* visual;
            Check(_dcomp->CreateVisual(&visual), "CreateVisual");
            _dcompVisual = visual;
            Check(_dcompVisual->SetContent((IUnknown*)_swapChain), "Visual.SetContent");
            Check(_dcompTarget->SetRoot(_dcompVisual), "Target.SetRoot");
            Check(_dcomp->Commit(), "DComp.Commit");
        }

        CreateRtvs();
        _frameIndex = _swapChain->GetCurrentBackBufferIndex();
    }

    private void CreateRtvs()
    {
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            ID3D12Resource* buf;
            Check(_swapChain->GetBuffer(i, __uuidof<ID3D12Resource>(), (void**)&buf), "GetBuffer");
            _backBuffers[i] = buf;
            D3D12MemoryDiagnostics.Track(buf, $"Swapchain.BackBuffer[{i}] {_w}x{_h}", (ulong)_w * _h * 4UL);
            _device->CreateRenderTargetView(buf, null, rtv);
            rtv.ptr += _rtvSize;
        }
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        // Normally throttle to present cadence before producing this frame. A keep-alive repaint fired from inside an
        // OS modal move/size loop (host called SuppressLatencyWaitOnce) skips it so the WndProc thread isn't blocked
        // up to a vblank — the drag-start/live-resize hitch. Self-resetting: one suppressed wait per call.
        if (_skipLatencyOnce) _skipLatencyOnce = false;
        else WaitForLatency();   // bound queued-frame latency before starting this frame's production
        _frameIndex = _swapChain->GetCurrentBackBufferIndex();
        WaitForFrame(_frameIndex);
        ID3D12CommandAllocator* allocator = _allocators[_frameIndex];
        Check(allocator->Reset(), "allocator.Reset");
        Check(_cmdList->Reset(allocator, null), "cmdList.Reset");
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

        if (StreamHasLayer(drawList))
        {
            // Layered path (acrylic AND/OR opacity groups): render the scene into an engine-owned canvas, run each
            // layer's passes in stream order, blit to the back buffer.
            _acrylic!.EnsureSize(_w, _h);
            _opacity!.EnsureSize(_w, _h);
            SubmitWithLayers(drawList, ctx, lw, lh, rtv);
        }
        else
        {
            _acrylic?.TickIdle(_fence->GetCompletedValue());   // age/trim the layer RT pools while no layer is on screen
            _opacity?.TickIdle(_fence->GetCompletedValue());
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
        Diag.Set("d3d12", "rects", _frameRectCount);
        Diag.Set("d3d12", "glyphInstances", _frameGlyphInstanceCount);
        Diag.Set("d3d12", "images", _frameImageCount);
        Diag.Set("d3d12", "imagesSkipped", _frameImageSkipped);   // >0 ⇒ a recorded image had no live texture this frame
        Diag.Set("d3d12", "imageAtlas", _imageTextures!.AtlasImages);   // thumbnails (<=128) packed into shared atlas pages
        Diag.Set("d3d12", "imagePool", _imageTextures.PoolImages);      // art (256/512) in reused per-bucket pool textures
        Diag.Set("text.atlas", "cachedGlyphs", _glyphs!.CachedGlyphs);
        Diag.Set("text.atlas", "nonZeroBytes", _glyphs.AtlasNonZero);
        Diag.Set("text.run", "cachedRuns", _glyphs.CachedRuns);      // shaped runs held across frames
        Diag.Set("text.run", "runsCached", _glyphs.RunsCached);      // this frame: runs served from the shaped-run cache
        Diag.Set("text.run", "runsShaped", _glyphs.RunsShaped);      // this frame: runs (re)shaped — should be ~0 in steady state

        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        Check(_cmdList->Close(), "cmdList.Close");

        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        _queue->ExecuteCommandLists(1, &execList);
        SignalFrame(_frameIndex);
    }

    // Decode completion → resident GPU texture (media-pipeline §4.1). Staged here (heap/upload only, no command list);
    // the CopyTextureRegion is recorded by FlushUploads at the top of the next SubmitDrawList.
    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
        => _imageTextures?.Stage(imageId, pbgra8, w, h);

    // Residency evicted the image → free its GPU texture (deferred behind the frame fence in the store).
    public void EvictImage(int imageId) => _imageTextures?.Free(imageId);

    private void ClearInsts() { _rectInsts.Clear(); _glyphInsts.Clear(); _shadowInsts.Clear(); _arcInsts.Clear(); _polylineInsts.Clear(); _gradInsts.Clear(); _imageDraws.Clear(); _runs.Clear(); }

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

    private void SetFullScissor()
    {
        RECT scd = new() { left = 0, top = 0, right = (int)_w, bottom = (int)_h };
        _cmdList->RSSetScissorRects(1, &scd);
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

    private void SetScissor(in RectF r)
    {
        RECT sc = ToScissor(r);
        _cmdList->RSSetScissorRects(1, &sc);
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
        SetScissor(clip.DeviceRect);
    }

    private void PopScissor()
    {
        if (_clipStack.Count > 0) _clipStack.RemoveAt(_clipStack.Count - 1);
        if (_roundedClipStack.Count > 0) _roundedClipStack.RemoveAt(_roundedClipStack.Count - 1);
        ApplyCurrentScissor();
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
        if (_clipStack.Count == 0) SetFullScissor();
        else SetScissor(_clipStack[^1]);
    }

    private void RecordAll(float lw, float lh)
    {
        // Replay non-glyph primitives in painter (stream) order so a shadow sits OVER the background drawn before it and
        // UNDER its own element. Consecutive same-kind ops are still one batched draw (each pipeline's Record/Begin fully
        // (re)binds its state, so interleaving pipelines is safe). Glyphs always render last — text on top within a z-context.
        if (_runs.Count > 0)
        {
            var rectSpan = CollectionsMarshal.AsSpan(_rectInsts);
            var shadowSpan = CollectionsMarshal.AsSpan(_shadowInsts);
            var arcSpan = CollectionsMarshal.AsSpan(_arcInsts);
            var polylineSpan = CollectionsMarshal.AsSpan(_polylineInsts);
            var gradSpan = CollectionsMarshal.AsSpan(_gradInsts);
            int rc = 0, sc = 0, ac = 0, pc = 0, gc = 0, ic = 0;
            foreach (var (kind, count) in _runs)
            {
                switch (kind)
                {
                    case PrimKind.Shadow: _shadowPipe!.Record(_cmdList, shadowSpan.Slice(sc, count), lw, lh); sc += count; break;
                    case PrimKind.Arc: _arcPipe!.Record(_cmdList, arcSpan.Slice(ac, count), lw, lh); ac += count; break;
                    case PrimKind.Polyline: _polylinePipe!.Record(_cmdList, polylineSpan.Slice(pc, count), lw, lh); pc += count; break;
                    case PrimKind.Gradient: _gradPipe!.Record(_cmdList, gradSpan.Slice(gc, count), lw, lh); gc += count; break;
                    case PrimKind.Rect: _rectPipe!.Record(_cmdList, rectSpan.Slice(rc, count), lw, lh); rc += count; break;
                    case PrimKind.Image:
                        _imagePipe!.Begin(_cmdList, _imageTextures!.Heap, lw, lh);   // (re)bind heap/PSO/root-sig/VB for this image run
                        for (int k = 0; k < count; k++)
                        {
                            var (inst, id) = _imageDraws[ic + k];
                            if (_imageTextures.TryGet(id, out var srv, out var uv))
                            {
                                var d = inst;
                                // Compose the atlas cell (uv) with the content-fit sub-rect baked on the instance (inst.Uv*,
                                // 0..1 source space): origin = cell.origin + fit.origin·cell.size, size = cell.size·fit.size.
                                // Whole-texture images (cell = 0,0,1,1) pass the content-fit rect through unchanged.
                                d.UvX = uv.X + inst.UvX * uv.W; d.UvY = uv.Y + inst.UvY * uv.H;
                                d.UvW = uv.W * inst.UvW; d.UvH = uv.H * inst.UvH;
                                _imagePipe.Draw(_cmdList, srv, in d);
                            }
                            else _frameImageSkipped++;   // image recorded but its texture isn't live yet (diagnostic: should be 0 once loaded)
                        }
                        ic += count;
                        break;
                }
            }
        }
        if (_glyphInsts.Count > 0) _glyphs!.Record(_cmdList, _glyphInsts, lw, lh);
    }

    private void FlushSegment(float lw, float lh)
    {
        _glyphs!.UploadIfDirty(_cmdList);
        RecordAll(lw, lh);
        ClearInsts();
    }

    private void SubmitStreaming(ReadOnlySpan<byte> drawList, float lw, float lh)
    {
        ClearInsts();
        _clipStack.Clear();
        _roundedClipStack.Clear();
        int pos = 0;
        while (pos + sizeof(int) <= drawList.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(drawList.Slice(pos));
            if (op == DrawOp.PushClip)
            {
                pos += sizeof(int);
                var clip = MemoryMarshal.Read<ClipCmd>(drawList.Slice(pos));
                pos += Unsafe.SizeOf<ClipCmd>();
                FlushSegment(lw, lh);
                PushScissor(in clip);
                continue;
            }
            if (op == DrawOp.PopClip)
            {
                pos += sizeof(int);
                FlushSegment(lw, lh);
                PopScissor();
                continue;
            }
            pos = DecodeOne(drawList, pos);
        }
        FlushSegment(lw, lh);
        _clipStack.Clear();
        _roundedClipStack.Clear();
        SetFullScissor();
    }

    // Layered path: render the scene into the canvas, processing the stream in order. An Acrylic PushLayer blurs the
    // backdrop drawn so far then composites the frosted surface (content draws on top; its PopLayer is a no-op). An
    // Opacity PushLayer redirects drawing into a leased transparent canvas-sized RT; its PopLayer composites that RT
    // once over the underlying target at GroupAlpha (flat group — no double-blend). Kinds nest via _layerKinds.
    private void SubmitWithLayers(ReadOnlySpan<byte> drawList, in FrameInfo ctx, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        // completed fence gates pool retire/drain; (frameIndex & 1) selects this frame's parity-banked SRV slots.
        _acrylic!.BeginCanvas(_cmdList, ctx.Clear, _fence->GetCompletedValue(), (int)(_frameIndex & 1));
        _opacity!.BeginFrame(_fence->GetCompletedValue(), (int)(_frameIndex & 1));
        ClearInsts();
        _clipStack.Clear();
        _roundedClipStack.Clear();
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
                FlushSegment(lw, lh);
                PushScissor(in clip);
                continue;
            }
            if (op == DrawOp.PopClip)
            {
                pos += sizeof(int);
                FlushSegment(lw, lh);
                PopScissor();
                continue;
            }
            if (op == DrawOp.PushLayer)
            {
                int p2 = pos + sizeof(int);
                var L = MemoryMarshal.Read<PushLayerCmd>(drawList.Slice(p2));
                pos = p2 + Unsafe.SizeOf<PushLayerCmd>();
                FlushSegment(lw, lh);                       // draw the backdrop-so-far into the current target
                if (L.Kind == (int)LayerKind.Opacity || L.Kind == (int)LayerKind.Blur)
                {
                    // Lease + clear + bind the group RT; scissor state carries over so the subtree clips identically.
                    // A Blur group carries its σ; on pop the RT is gaussian-blurred before the flat composite.
                    int slot = _opacity.Acquire(_cmdList, _fenceValue + 1);
                    _opacityGroups.Add((slot, L.GroupAlpha, L.Kind == (int)LayerKind.Blur ? L.BlurSigma : 0f));
                    _layerKinds.Add(L.Kind);
                }
                else
                {
                    // region snapshot → pooled-RT separable blur → acrylic composite (re-binds canvas + full viewport).
                    // _fenceValue + 1 is the fence value SignalFrame will signal for THIS frame (gates pooled-RT retire).
                    // Documented limitation: nested inside an open opacity group, the acrylic composites into the
                    // CANVAS (the acrylic leaf owns its canvas binding) — combine acrylic + group on one node instead.
                    _acrylic.BlurAndComposite(_cmdList, L, lw, lh, _frameScale, _fenceValue + 1);
                    if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);   // back to the open group RT
                    ApplyCurrentScissor();
                    _layerKinds.Add((int)LayerKind.Acrylic);
                }
                continue;
            }
            if (op == DrawOp.PopLayer)
            {
                pos += sizeof(int) + Unsafe.SizeOf<PopLayerCmd>();
                int kind = _layerKinds.Count > 0 ? _layerKinds[^1] : (int)LayerKind.Acrylic;
                if (_layerKinds.Count > 0) _layerKinds.RemoveAt(_layerKinds.Count - 1);
                if ((kind == (int)LayerKind.Opacity || kind == (int)LayerKind.Blur) && _opacityGroups.Count > 0)
                {
                    FlushSegment(lw, lh);                   // finish the subtree into the group RT
                    var (slot, alpha, sigma) = _opacityGroups[^1];
                    _opacityGroups.RemoveAt(_opacityGroups.Count - 1);
                    // Self-blur group: gaussian-blur the group RT in place (leaves it readable); else just BeginRead it.
                    if (kind == (int)LayerKind.Blur && sigma > 0f) _opacity.BlurInPlace(_cmdList, slot, sigma, _fenceValue + 1);
                    else _opacity.BeginRead(_cmdList, slot);
                    // Composite over the UNDERLYING target: the enclosing group's RT, or the canvas.
                    if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);
                    else _acrylic.BindCanvas(_cmdList);
                    _opacity.Composite(_cmdList, slot, alpha);
                    _opacity.Release(slot);
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
            var (slot, alpha, sigma) = _opacityGroups[^1];
            _opacityGroups.RemoveAt(_opacityGroups.Count - 1);
            if (sigma > 0f) _opacity.BlurInPlace(_cmdList, slot, sigma, _fenceValue + 1);
            else _opacity.BeginRead(_cmdList, slot);
            if (_opacityGroups.Count > 0) _opacity.Bind(_cmdList, _opacityGroups[^1].Slot);
            else _acrylic.BindCanvas(_cmdList);
            _opacity.Composite(_cmdList, slot, alpha);
            _opacity.Release(slot);
        }
        _layerKinds.Clear();
        _clipStack.Clear();
        _roundedClipStack.Clear();
        _acrylic.BlitToBackBuffer(_cmdList, backRtv);
    }

    private static bool StreamHasLayer(ReadOnlySpan<byte> cmds)
    {
        int pos = 0;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect: pos += Unsafe.SizeOf<FillRoundRectCmd>(); break;
                case DrawOp.DrawGlyphRun: pos += Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
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
                case DrawOp.PushLayer: return true;
                case DrawOp.PopLayer: pos += Unsafe.SizeOf<PopLayerCmd>(); break;
                default: return false;
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

    internal void Present()
    {
        // ALLOW_TEARING is valid only with sync-interval 0 on a tearing-capable (non-composited) swapchain.
        uint interval = _vsync ? 1u : 0u;
        uint flags = (!_vsync && _tearingSupported) ? DXGI.DXGI_PRESENT_ALLOW_TEARING : 0u;
        Check(_swapChain->Present(interval, flags), "Present");
    }

    private void SignalFrame(uint frameIndex)
    {
        ulong v = ++_fenceValue;
        Check(_queue->Signal(_fence, v), "queue.Signal");
        _frameFenceValues[frameIndex] = v;
    }

    private void WaitForFrame(uint frameIndex)
    {
        ulong v = _frameFenceValues[frameIndex];
        if (v == 0 || _fence->GetCompletedValue() >= v) return;
        Check(_fence->SetEventOnCompletion(v, _fenceEvent), "SetEventOnCompletion");
        WaitForSingleObject(_fenceEvent, INFINITE);
    }

    // Block until the swapchain is ready to accept a new frame (bounds present-queue depth → lower latency, efficient wait).
    // Bounded timeout so a lost device can't hang the loop. No-op if the waitable wasn't created (older DXGI / failure).
    private void WaitForLatency()
    {
        if (_hasLatencyWaitable) WaitForSingleObject(_frameLatencyWaitable, 1000);
    }

    private bool _skipLatencyOnce;   // set by SuppressLatencyWaitOnce, consumed by the next SubmitDrawList

    public void SuppressLatencyWaitOnce() => _skipLatencyOnce = true;

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
        Check(_queue->Signal(_fence, v), "queue.Signal");
        if (_fence->GetCompletedValue() < v)
        {
            Check(_fence->SetEventOnCompletion(v, _fenceEvent), "SetEventOnCompletion");
            WaitForSingleObject(_fenceEvent, INFINITE);
        }
    }

    /// <summary>
    /// Debug-only: read the last-rendered back buffer back to CPU as tightly-packed, top-down BGRA8. Used by the
    /// <c>--screenshot</c> tooling to produce a PNG for visual fidelity diffing — NOT a hot-path method (it stalls
    /// the GPU). With FLIP_DISCARD the just-presented buffer at <c>_frameIndex</c> is still intact until reused.
    /// </summary>
    public byte[] CaptureBgra(out int width, out int height)
    {
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
        _queue->ExecuteCommandLists(1, &execList);
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

    internal void Resize(uint w, uint h)
    {
        if (w < 1) w = 1; if (h < 1) h = 1;
        if (w == _w && h == _h) return;
        WaitForGpu();
        D3D12MemoryDiagnostics.Resize("Swapchain", w, h);
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            if (_backBuffers[i] != null)
            {
                D3D12MemoryDiagnostics.Release(_backBuffers[i], $"Swapchain.BackBuffer[{i}]");
                _backBuffers[i]->Release();
                _backBuffers[i] = null;
            }
        }
        Check(_swapChain->ResizeBuffers(FRAME_COUNT, w, h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, _swapChainFlags), "ResizeBuffers");
        _w = w; _h = h;
        CreateRtvs();
        _frameIndex = _swapChain->GetCurrentBackBufferIndex();
    }

    public Size2 SizePx => new(_w, _h);

    public void Dispose()
    {
        if (_device != null) WaitForGpu();
        if (_dcompVisual != null) _dcompVisual->Release();
        if (_dcompTarget != null) _dcompTarget->Release();
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
        for (uint i = 0; i < FRAME_COUNT; i++)
        {
            if (_backBuffers[i] != null)
            {
                D3D12MemoryDiagnostics.Release(_backBuffers[i], $"Swapchain.BackBuffer[{i}]");
                _backBuffers[i]->Release();
                _backBuffers[i] = null;
            }
        }
        D3D12MemoryDiagnostics.Snapshot("D3D12Device.Dispose");
        if (_swapChain != null) _swapChain->Release();
        if (_rtvHeap != null) { D3D12MemoryDiagnostics.Release(_rtvHeap, "Device.RtvHeap"); _rtvHeap->Release(); }
        if (_cmdList != null) _cmdList->Release();
        for (uint i = 0; i < FRAME_COUNT; i++)
            if (_allocators[i] != null) _allocators[i]->Release();
        if (_fence != null) _fence->Release();
        if (_queue != null) _queue->Release();
        if (_factory != null) _factory->Release();
        if (_device != null) _device->Release();
        if (_fenceEvent != HANDLE.NULL) CloseHandle(_fenceEvent);
    }
}

public sealed unsafe class D3D12Swapchain : ISwapchain
{
    private readonly D3D12Device _device;
    public D3D12Swapchain(D3D12Device device) => _device = device;
    public Size2 SizePx => _device.SizePx;
    public void Resize(Size2 px) => _device.Resize((uint)px.Width, (uint)px.Height);
    public void Present() => _device.Present();
    public void Dispose() { }
}
