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
    private const uint FRAME_COUNT = 2;
    private const uint INFINITE = 0xFFFFFFFF;

    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private IDXGIFactory4* _factory;
    private IDXGISwapChain3* _swapChain;
    private ID3D12DescriptorHeap* _rtvHeap;
    private uint _rtvSize;
    private readonly ID3D12Resource*[] _backBuffers = new ID3D12Resource*[FRAME_COUNT];
    private ID3D12CommandAllocator* _allocator;
    private ID3D12GraphicsCommandList* _cmdList;
    private ID3D12Fence* _fence;
    private ulong _fenceValue;
    private HANDLE _fenceEvent;

    private HWND _hwnd;
    private uint _w, _h;
    private uint _frameIndex;
    private RoundRectPipeline? _rectPipe;
    private readonly List<RectInstance> _rectInsts = new();
    private ShadowPipeline? _shadowPipe;
    private readonly List<ShadowInstance> _shadowInsts = new();
    private GradientPipeline? _gradPipe;
    private readonly List<GradientInstance> _gradInsts = new();
    private readonly List<RectF> _clipStack = new(16);
    private AcrylicCompositor? _acrylic;
    private GlyphRenderer? _glyphs;
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
        _gradPipe = new GradientPipeline();
        _gradPipe.Init(_device);
        _acrylic = new AcrylicCompositor();
        _acrylic.Init(_device);
        _glyphs = new GlyphRenderer();
        _glyphs.Init(_device);
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

        ID3D12CommandAllocator* alloc;
        Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator");
        _allocator = alloc;

        ID3D12GraphicsCommandList* list;
        Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, _allocator, null,
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
        sd.Flags = 0;

        IDXGISwapChain1* sc1;
        if (_composited)
            Check(_factory->CreateSwapChainForComposition((IUnknown*)_queue, &sd, null, &sc1), "CreateSwapChainForComposition");
        else
            Check(_factory->CreateSwapChainForHwnd((IUnknown*)_queue, _hwnd, &sd, null, null, &sc1), "CreateSwapChainForHwnd");

        IDXGISwapChain3* sc3;
        Check(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3");
        sc1->Release();
        _swapChain = sc3;

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
        _frameIndex = _swapChain->GetCurrentBackBufferIndex();
        Check(_allocator->Reset(), "allocator.Reset");
        Check(_cmdList->Reset(_allocator, null), "cmdList.Reset");

        ID3D12Resource* backBuffer = _backBuffers[_frameIndex];
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += _frameIndex * _rtvSize;

        // DPI: render at native resolution but map DIP coordinates via a LOGICAL viewport (= physical / scale),
        // while glyphs are rasterized at physical px → crisp + correctly sized at high DPI.
        _frameScale = ctx.Scale <= 0f ? 1f : ctx.Scale;
        _frameRectCount = 0;
        _frameGlyphInstanceCount = 0;
        _rectPipe!.BeginFrame();
        _shadowPipe!.BeginFrame();
        _gradPipe!.BeginFrame();
        _glyphs!.BeginFrame();
        float lw = _w / _frameScale, lh = _h / _frameScale;

        if (StreamHasLayer(drawList))
        {
            // Acrylic path: render the scene into an engine-owned canvas, blur+composite at each layer, blit to the back buffer.
            _acrylic!.EnsureSize(_w, _h);
            SubmitWithLayers(drawList, ctx, lw, lh, rtv);
        }
        else
        {
            _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
            float* clear = stackalloc float[4] { ctx.Clear.R, ctx.Clear.G, ctx.Clear.B, ctx.Clear.A };
            _cmdList->ClearRenderTargetView(rtv, clear, 0, null);
            SetFullViewport();
            SubmitStreaming(drawList, lw, lh);
        }
        Diag.Set("d3d12", "rects", _frameRectCount);
        Diag.Set("d3d12", "glyphInstances", _frameGlyphInstanceCount);
        Diag.Set("text.atlas", "cachedGlyphs", _glyphs!.CachedGlyphs);
        Diag.Set("text.atlas", "nonZeroBytes", _glyphs.AtlasNonZero);

        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        Check(_cmdList->Close(), "cmdList.Close");

        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        _queue->ExecuteCommandLists(1, &execList);
    }

    private void ClearInsts() { _rectInsts.Clear(); _glyphInsts.Clear(); _shadowInsts.Clear(); _gradInsts.Clear(); }

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
                    _rectInsts.Add(new RectInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        RTL = c.Radii.TopLeft, RTR = c.Radii.TopRight, RBR = c.Radii.BottomRight, RBL = c.Radii.BottomLeft,
                        R = c.Fill.R, G = c.Fill.G, B = c.Fill.B, A = c.Fill.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity,
                    });
                    _frameRectCount++;
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
                        _glyphs!.LayoutRun(s, _strings.Resolve(g.Family), g.FontSize, g.Bold != 0, g.Bounds.X, g.Bounds.Y, g.Bounds.W, g.Wrap, g.Trim, g.MaxLines, g.Color, _frameScale, g.Transform, g.Opacity, _glyphInsts);
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
                    if (im.Ready != 0) AddReadyImageArt(in im);
                    else AddImagePlaceholder(in im);
                    break;
                }
                case DrawOp.DrawRoundRectStroke:
                {
                    var c = MemoryMarshal.Read<DrawRoundRectStrokeCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>();
                    _rectInsts.Add(new RectInstance
                    {
                        PosX = c.Rect.X, PosY = c.Rect.Y, W = c.Rect.W, H = c.Rect.H,
                        RTL = c.Radii.TopLeft, RTR = c.Radii.TopRight, RBR = c.Radii.BottomRight, RBL = c.Radii.BottomLeft,
                        R = c.Color.R, G = c.Color.G, B = c.Color.B, A = c.Color.A,
                        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
                        Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, StrokeWidth = c.StrokeWidth,
                    });
                    _frameRectCount++;
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
    }

    private void AddReadyImageArt(in DrawImageCmd im)
    {
        ColorF a = AlbumColor(im.ImageId, 0);
        ColorF b = AlbumColor(im.ImageId, 1);
        ColorF c = AlbumColor(im.ImageId, 2);
        ColorF d = ColorF.Lerp(AlbumColor(im.ImageId, 3), ColorF.FromRgba(8, 10, 18), 0.35f);

        _gradInsts.Add(new GradientInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            StartX = 0f, StartY = 0.05f, EndX = 1f, EndY = 1f,
            C0R = a.R, C0G = a.G, C0B = a.B, C0A = a.A,
            C1R = b.R, C1G = b.G, C1B = b.B, C1A = b.A,
            C2R = c.R, C2G = c.G, C2B = c.B, C2A = c.A,
            C3R = d.R, C3G = d.G, C3B = d.B, C3A = d.A,
            O0 = 0f, O1 = 0.42f, O2 = 0.74f, O3 = 1f,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy, Radius = im.Radii.TopLeft, Opacity = im.Opacity,
            Shape = 0f, StopCount = 4f,
        });

        ColorF light = ColorF.FromRgba(255, 255, 255, 72);
        ColorF tint = ColorF.Lerp(a, ColorF.FromRgba(255, 255, 255), 0.35f) with { A = 0.28f };
        ColorF clear = ColorF.Transparent;
        _gradInsts.Add(new GradientInstance
        {
            PosX = im.Rect.X, PosY = im.Rect.Y, W = im.Rect.W, H = im.Rect.H,
            StartX = 0.12f, StartY = 0.10f, EndX = 1f, EndY = 1f,
            C0R = light.R, C0G = light.G, C0B = light.B, C0A = light.A,
            C1R = tint.R, C1G = tint.G, C1B = tint.B, C1A = tint.A,
            C2R = clear.R, C2G = clear.G, C2B = clear.B, C2A = clear.A,
            C3R = clear.R, C3G = clear.G, C3B = clear.B, C3A = clear.A,
            O0 = 0f, O1 = 0.25f, O2 = 0.58f, O3 = 1f,
            M11 = im.Transform.M11, M12 = im.Transform.M12, M21 = im.Transform.M21, M22 = im.Transform.M22,
            Dx = im.Transform.Dx, Dy = im.Transform.Dy, Radius = im.Radii.TopLeft, Opacity = im.Opacity,
            Shape = 1f, StopCount = 4f,
        });
    }

    private static ColorF AlbumColor(int imageId, int stop)
    {
        uint x = unchecked((uint)imageId * 747796405u + (uint)stop * 2891336453u + 0x9E3779B9u);
        x ^= x >> 16;
        x *= 2246822519u;
        x ^= x >> 13;
        x *= 3266489917u;
        x ^= x >> 16;
        byte r = (byte)(44 + (x & 0x9Fu));
        byte g = (byte)(48 + ((x >> 8) & 0x9Fu));
        byte b = (byte)(60 + ((x >> 16) & 0x8Fu));
        return ColorF.FromRgba(r, g, b);
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

    private void PushScissor(in RectF r)
    {
        _clipStack.Add(r);
        SetScissor(r);
    }

    private void PopScissor()
    {
        if (_clipStack.Count > 0) _clipStack.RemoveAt(_clipStack.Count - 1);
        ApplyCurrentScissor();
    }

    private void ApplyCurrentScissor()
    {
        if (_clipStack.Count == 0) SetFullScissor();
        else SetScissor(_clipStack[^1]);
    }

    private void RecordAll(float lw, float lh)
    {
        if (_shadowInsts.Count > 0) _shadowPipe!.Record(_cmdList, CollectionsMarshal.AsSpan(_shadowInsts), lw, lh);
        if (_gradInsts.Count > 0) _gradPipe!.Record(_cmdList, CollectionsMarshal.AsSpan(_gradInsts), lw, lh);
        if (_rectInsts.Count > 0) _rectPipe!.Record(_cmdList, CollectionsMarshal.AsSpan(_rectInsts), lw, lh);
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
                PushScissor(clip.DeviceRect);
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
        SetFullScissor();
    }

    // Acrylic path: render the scene into the canvas, processing the stream in order so each PushLayer blurs the
    // backdrop drawn so far, then composites the acrylic and lets the layer's content draw on top.
    private void SubmitWithLayers(ReadOnlySpan<byte> drawList, in FrameInfo ctx, float lw, float lh, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        _acrylic!.BeginCanvas(_cmdList, ctx.Clear);
        ClearInsts();
        _clipStack.Clear();
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
                PushScissor(clip.DeviceRect);
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
                FlushSegment(lw, lh);                       // draw the backdrop-so-far into the canvas
                _acrylic.BlurAndComposite(_cmdList, L, lw, lh);  // blur + acrylic composite (re-binds canvas + full viewport)
                ApplyCurrentScissor();
                continue;
            }
            if (op == DrawOp.PopLayer) { pos += sizeof(int) + Unsafe.SizeOf<PopLayerCmd>(); continue; }
            pos = DecodeOne(drawList, pos);
        }
        FlushSegment(lw, lh);
        _clipStack.Clear();
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
                case DrawOp.DrawGradientRect: pos += Unsafe.SizeOf<DrawGradientRectCmd>(); break;
                case DrawOp.DrawGradientStroke: pos += Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
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

    internal void PresentAndSync()
    {
        Check(_swapChain->Present(1, 0), "Present");
        WaitForGpu();
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
        Check(_swapChain->ResizeBuffers(FRAME_COUNT, w, h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 0), "ResizeBuffers");
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
        _shadowPipe?.Dispose();
        _gradPipe?.Dispose();
        _acrylic?.Dispose();
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
        if (_rtvHeap != null) _rtvHeap->Release();
        if (_cmdList != null) _cmdList->Release();
        if (_allocator != null) _allocator->Release();
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
    public void Present() => _device.PresentAndSync();
    public void Dispose() { }
}
