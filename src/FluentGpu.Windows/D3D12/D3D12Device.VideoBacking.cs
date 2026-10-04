using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    /// <summary>Edge length (px) of the shared solid-black backing content; the presenter scales it to each slot's placed rect.</summary>
    internal const uint VideoBackingSize = 16;

    // The ONE opaque-black composition content every video slot's backing visual shows (Chromium's solid-colour pool, with a
    // pool of one: a visual's content may be shared). The DComp device has no rendering device (DCompositionCreateDevice(null)),
    // so IDCompositionDevice::CreateSurface cannot be drawn into: this is a tiny composition swapchain on the D3D12 queue,
    // cleared to opaque black and presented ONCE — DWM keeps showing that frame, so it costs nothing per frame. Created lazily
    // by the first video slot, released with the device and recreated after a device recovery (the queue it was built on dies).
    private IDXGISwapChain3* _videoBacking;
    private bool _videoBackingFailed;   // creation failed once on this device: logged once, not retried (no GPU-wait retry storm)

    /// <summary>
    /// The shared opaque-black backing content for <c>DCompVideoPresenter</c>'s backing visuals (the caller adds its own
    /// reference through <c>IDCompositionVisual::SetContent</c>; this device keeps the owning one). Created on first use;
    /// null when it could not be created (the slot then has no backing — the old see-through behaviour — never a throw into
    /// the render thread's Commit path). Render-thread-confined.
    /// </summary>
    internal IUnknown* EnsureVideoBacking()
    {
        AssertSubmitThread();
        if (_videoBacking == null && !_videoBackingFailed)
        {
            if (!TryCreateVideoBacking())
                _videoBackingFailed = true;
        }
        return (IUnknown*)_videoBacking;
    }

    private void ReleaseVideoBacking()
    {
        if (_videoBacking != null) { _videoBacking->Release(); _videoBacking = null; }
        _videoBackingFailed = false;   // a recovered device gets a fresh attempt
    }

    private bool TryCreateVideoBacking()
    {
        if (_device == null || _factory == null || _queue == null) return false;
        IDXGISwapChain1* sc1 = null;
        IDXGISwapChain3* sc3 = null;
        ID3D12Resource* buf = null;
        ID3D12DescriptorHeap* rtvHeap = null;
        ID3D12CommandAllocator* alloc = null;
        ID3D12GraphicsCommandList* cl = null;
        try
        {
            DXGI_SWAP_CHAIN_DESC1 sd = default;
            sd.Width = VideoBackingSize;
            sd.Height = VideoBackingSize;
            sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            sd.SampleDesc.Count = 1;
            sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            sd.BufferCount = FRAME_COUNT;
            sd.Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH;
            sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
            sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;   // opaque by construction: the backing must never be see-through
            if (BackingFailed(_factory->CreateSwapChainForComposition((IUnknown*)_queue, &sd, null, &sc1), "CreateSwapChainForComposition(video backing)")) return false;
            if (BackingFailed(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3 (video backing)")) return false;
            if (BackingFailed(sc3->GetBuffer(0, __uuidof<ID3D12Resource>(), (void**)&buf), "GetBuffer(video backing)")) return false;

            D3D12_DESCRIPTOR_HEAP_DESC hd = default;
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            hd.NumDescriptors = 1;
            if (BackingFailed(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&rtvHeap), "CreateDescriptorHeap(video backing)")) return false;
            D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
            _device->CreateRenderTargetView(buf, null, rtv);

            if (BackingFailed(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                    __uuidof<ID3D12CommandAllocator>(), (void**)&alloc), "CreateCommandAllocator(video backing)")) return false;
            if (BackingFailed(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, null,
                    __uuidof<ID3D12GraphicsCommandList>(), (void**)&cl), "CreateCommandList(video backing)")) return false;

            D3D12_RESOURCE_BARRIER b = default;
            b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b.Anonymous.Transition.pResource = buf;
            b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
            b.Anonymous.Transition.StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
            b.Anonymous.Transition.StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
            cl->ResourceBarrier(1, &b);
            float* black = stackalloc float[4] { 0f, 0f, 0f, 1f };
            cl->ClearRenderTargetView(rtv, black, 0, null);
            b.Anonymous.Transition.StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
            b.Anonymous.Transition.StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
            cl->ResourceBarrier(1, &b);
            HRESULT closeHr = cl->Close();
            if (BackingFailed(closeHr, "cmdList.Close(video backing)"))
            {
                DrainDebugLayerMessages();
                return false;
            }
            ID3D12CommandList* rawList = (ID3D12CommandList*)cl;
            _queue->ExecuteCommandLists(1, &rawList);
            // One present on the queue, in order after the clear. The CPU wait keeps the one-off list/allocator/heap alive
            // until the GPU is done with them (cold path: once per device, on the first video slot). It runs even when the
            // Present failed (the clear is still in flight), and a throwing wait is swallowed: this is reached from the
            // presenter's Commit path, which must never throw.
            HRESULT presentHr = (HRESULT)global::FluentGpu.Interop.Generated.IDXGISwapChainVtbl.Present(sc3, 0, 0);
            try { WaitForGpu(); }
            catch (InvalidOperationException ex)
            {
                Diag.Line($"[video.presenter] video backing GPU wait failed: {ex.Message}; video slots have no opaque backing");
                return false;
            }
            if (BackingFailed(presentHr, "Present(video backing)")) return false;

            _videoBacking = sc3;
            sc3 = null;   // ownership moved to the device; the finally must not release it
            return true;
        }
        finally
        {
            if (cl != null) cl->Release();
            if (alloc != null) alloc->Release();
            if (rtvHeap != null) rtvHeap->Release();
            if (buf != null) buf->Release();
            if (sc3 != null) sc3->Release();
            if (sc1 != null) sc1->Release();
        }
    }

    // Non-throwing HRESULT check for the backing's creation: true = FAILED. The first failure logs once (Diag.Line is
    // always-on); EnsureVideoBacking then latches _videoBackingFailed so nothing retries.
    private static bool BackingFailed(HRESULT hr, string what)
    {
        if ((int)hr >= 0) return false;
        Diag.Line($"[video.presenter] {what} failed: 0x{(uint)hr:X8}; video slots have no opaque backing");
        return true;
    }
}
