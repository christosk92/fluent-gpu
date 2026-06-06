using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

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
    private GlyphRenderer? _glyphs;
    private readonly List<GlyphInstance> _glyphInsts = new();
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
        _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);

        float* clear = stackalloc float[4] { ctx.Clear.R, ctx.Clear.G, ctx.Clear.B, ctx.Clear.A };
        _cmdList->ClearRenderTargetView(rtv, clear, 0, null);

        D3D12_VIEWPORT vpd = new() { TopLeftX = 0, TopLeftY = 0, Width = _w, Height = _h, MinDepth = 0, MaxDepth = 1 };
        _cmdList->RSSetViewports(1, &vpd);
        RECT scd = new() { left = 0, top = 0, right = (int)_w, bottom = (int)_h };
        _cmdList->RSSetScissorRects(1, &scd);

        Decode(drawList);
        Diag.Set("d3d12", "rects", _rectInsts.Count);
        Diag.Set("d3d12", "glyphInstances", _glyphInsts.Count);
        Diag.Set("text.atlas", "cachedGlyphs", _glyphs!.CachedGlyphs);
        Diag.Set("text.atlas", "nonZeroBytes", _glyphs.AtlasNonZero);
        _glyphs.UploadIfDirty(_cmdList);   // copy newly-rasterized glyphs into the GPU atlas (before sampling)
        if (_rectInsts.Count > 0)
            _rectPipe!.Record(_cmdList, CollectionsMarshal.AsSpan(_rectInsts), _w, _h);
        if (_glyphInsts.Count > 0)
            _glyphs.Record(_cmdList, _glyphInsts, _w, _h);

        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        Check(_cmdList->Close(), "cmdList.Close");

        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        _queue->ExecuteCommandLists(1, &execList);
    }

    private void Decode(ReadOnlySpan<byte> cmds)
    {
        _rectInsts.Clear();
        _glyphInsts.Clear();
        int pos = 0;
        while (pos + sizeof(int) <= cmds.Length)
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
                    });
                    break;
                }
                case DrawOp.DrawGlyphRun:
                {
                    var g = MemoryMarshal.Read<DrawGlyphRunCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGlyphRunCmd>();
                    string s = _strings.Resolve(g.Text);
                    if (s.Length > 0)
                        _glyphs!.LayoutRun(s, g.FontSize, g.Bold != 0, g.Bounds.X, g.Bounds.Y, g.Color, _glyphInsts);
                    break;
                }
                default:
                    pos = cmds.Length;
                    break;
            }
        }
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
        for (uint i = 0; i < FRAME_COUNT; i++) { if (_backBuffers[i] != null) { _backBuffers[i]->Release(); _backBuffers[i] = null; } }
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
        _rectPipe?.Dispose();
        for (uint i = 0; i < FRAME_COUNT; i++) if (_backBuffers[i] != null) _backBuffers[i]->Release();
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
