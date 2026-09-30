using TerraFX.Interop.DirectX;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Cold-path construction helpers for the retained-tiles phases (docs/plans/scroll-gpu-retained-tiles-implementation.md
/// §B/§C), every call through the GEN-COM bindings generated from <c>d3d12.comabi.json</c> (no hand-typed slots):
/// the D3D12_OPTIONS5 <c>RenderPassesTier</c> query (logged once in [d3d12.present] by P2; render passes are callable at
/// every tier — tier 0 is emulated by the runtime), and the COPY / COMPUTE side queue, allocator, closed command list and
/// fence the P2 UploadQueue and compute Kawase bake build on. P0: NOT wired into rendering. Every method returns the
/// HRESULT and leaves its out pointer null on failure; the caller owns (Releases) what it gets.
/// </summary>
internal static unsafe class D3D12SideQueues
{
    /// <summary>Query <c>D3D12_FEATURE_DATA_D3D12_OPTIONS5.RenderPassesTier</c>.</summary>
    public static int QueryRenderPassesTier(void* device, out D3D12_RENDER_PASS_TIER tier)
    {
        D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5 = default;
        int hr = Gen.ID3D12DeviceVtbl.CheckFeatureSupport(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS5, &o5,
            (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS5));
        tier = hr >= 0 ? o5.RenderPassesTier : D3D12_RENDER_PASS_TIER.D3D12_RENDER_PASS_TIER_0;
        return hr;
    }

    /// <summary>A command queue of <paramref name="type"/> (<c>COPY</c> for uploads, <c>COMPUTE</c> for bakes).</summary>
    public static int CreateQueue(void* device, D3D12_COMMAND_LIST_TYPE type, out void* queue)
    {
        D3D12_COMMAND_QUEUE_DESC qd = default;
        qd.Type = type;
        qd.Flags = D3D12_COMMAND_QUEUE_FLAGS.D3D12_COMMAND_QUEUE_FLAG_NONE;
        Guid iid = Gen.ID3D12CommandQueueVtbl.IID;
        void* q = null;
        int hr = Gen.ID3D12DeviceVtbl.CreateCommandQueue(device, &qd, &iid, &q);
        queue = hr >= 0 ? q : null;
        return hr;
    }

    public static int CreateAllocator(void* device, D3D12_COMMAND_LIST_TYPE type, out void* allocator)
    {
        Guid iid = Gen.ID3D12CommandAllocatorVtbl.IID;
        void* a = null;
        int hr = Gen.ID3D12DeviceVtbl.CreateCommandAllocator(device, type, &iid, &a);
        allocator = hr >= 0 ? a : null;
        return hr;
    }

    /// <summary>A command list of <paramref name="type"/> over <paramref name="allocator"/>, returned CLOSED (the first
    /// use is <c>Reset</c>, like every per-turn list). <paramref name="asList4"/> QIs for
    /// <c>ID3D12GraphicsCommandList4</c> (the render-pass surface); otherwise the base graphics list.</summary>
    public static int CreateClosedList(void* device, D3D12_COMMAND_LIST_TYPE type, void* allocator, bool asList4, out void* list)
    {
        Guid iid = asList4 ? Gen.ID3D12GraphicsCommandList4Vtbl.IID : Gen.ID3D12GraphicsCommandListVtbl.IID;
        void* l = null;
        int hr = Gen.ID3D12DeviceVtbl.CreateCommandList(device, 0, type, allocator, null, &iid, &l);
        if (hr >= 0)
        {
            hr = Gen.ID3D12GraphicsCommandListVtbl.Close(l);
            if (hr < 0) { Gen.IUnknownVtbl.Release(l); l = null; }
        }
        list = hr >= 0 ? l : null;
        return hr;
    }

    public static int CreateFence(void* device, ulong initialValue, out void* fence)
    {
        Guid iid = Gen.ID3D12FenceVtbl.IID;
        void* f = null;
        int hr = Gen.ID3D12DeviceVtbl.CreateFence(device, initialValue, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, &iid, &f);
        fence = hr >= 0 ? f : null;
        return hr;
    }
}
