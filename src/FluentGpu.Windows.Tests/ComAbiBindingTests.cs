using System;
using System.Runtime.InteropServices;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The GEN-COM bindings generated from <c>d3d12.comabi.json</c> (com-interop.md: no human types a slot at a call site;
/// the ABI-golden backstop). Two layers:
/// <list type="bullet">
/// <item><b>Slot order</b> — every generated binding is invoked against a FAKE COM object whose vtable holds a "hit" stub
/// ONLY at the slot TerraFX's ClangSharp-generated <c>Vtbl&lt;T&gt;</c> layout gives that method (every other entry is a
/// "miss" stub), so a wrong slot in the manifest fails here, not as a GPU hang. The IIDs are checked against TerraFX's
/// <c>[Guid]</c>s. Pure: no device.</item>
/// <item><b>Runtime self-check</b> — the retained-tiles bindings (OPTIONS5 RenderPassesTier, COPY/COMPUTE queues + fence
/// signal/wait, ID3D12GraphicsCommandList4 Begin/EndRenderPass, IDXGISwapChain1::Present1 with dirty rects) exercised on
/// a WARP device, so the struct/enum parameter ABI is proven against the live runtime. Skipped where D3D12 is absent.</item>
/// </list>
/// </summary>
public sealed unsafe class ComAbiBindingTests
{
    // ── slot order ────────────────────────────────────────────────────────────────────────────────────────────────

    [ThreadStatic] private static int t_hits;
    [ThreadStatic] private static int t_misses;

    // The stubs take only `this`: every bound method passes integer/pointer/8-byte-struct arguments and returns an
    // integer or void, and on the Windows x64 and ARM64 ABIs the caller owns argument registers/stack and the return
    // register, so a callee that ignores the extra arguments is ABI-safe.
    [UnmanagedCallersOnly] private static nint Hit(void* self) { t_hits++; return 0; }
    [UnmanagedCallersOnly] private static nint Miss(void* self) { t_misses++; return 0; }

    private const int FakeVtblSlots = 96;

    /// <summary>True when <paramref name="invoke"/> dispatched through exactly vtable[<paramref name="slot"/>].</summary>
    private static bool DispatchesThrough(int slot, Action<nint> invoke)
    {
        nint* vtbl = (nint*)NativeMemory.AllocZeroed(FakeVtblSlots, (nuint)sizeof(nint));
        nint* obj = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        try
        {
            for (int i = 0; i < FakeVtblSlots; i++) vtbl[i] = (nint)(delegate* unmanaged<void*, nint>)&Miss;
            vtbl[slot] = (nint)(delegate* unmanaged<void*, nint>)&Hit;
            *obj = (nint)vtbl;
            t_hits = 0; t_misses = 0;
            invoke((nint)obj);
            return t_hits == 1 && t_misses == 0;
        }
        finally
        {
            NativeMemory.Free(obj);
            NativeMemory.Free(vtbl);
        }
    }

    /// <summary>The slot index of a TerraFX <c>Vtbl&lt;T&gt;</c> field (its byte offset / pointer size).</summary>
    private static int Slot<TVtbl>(ref TVtbl vtbl, void* field) where TVtbl : unmanaged
    {
        fixed (TVtbl* p = &vtbl) return (int)(((byte*)field - (byte*)p) / sizeof(nint));
    }

    [Fact]
    public void EveryGeneratedBindingDispatchesThroughTheSlotTerraFxHarvested()
    {
        var unk = default(IUnknown.Vtbl<IUnknown>);
        var fence = default(ID3D12Fence.Vtbl<ID3D12Fence>);
        var queue = default(ID3D12CommandQueue.Vtbl<ID3D12CommandQueue>);
        var sc = default(IDXGISwapChain.Vtbl<IDXGISwapChain>);
        var dev = default(ID3D12Device.Vtbl<ID3D12Device>);
        var alloc = default(ID3D12CommandAllocator.Vtbl<ID3D12CommandAllocator>);
        var list = default(ID3D12GraphicsCommandList.Vtbl<ID3D12GraphicsCommandList>);
        var list4 = default(ID3D12GraphicsCommandList4.Vtbl<ID3D12GraphicsCommandList4>);
        var sc1 = default(IDXGISwapChain1.Vtbl<IDXGISwapChain1>);

        (string Name, int Slot, Action<nint> Invoke)[] cases =
        [
            ("IUnknown.AddRef", Slot(ref unk, &unk.AddRef), o => Gen.IUnknownVtbl.AddRef((void*)o)),
            ("IUnknown.Release", Slot(ref unk, &unk.Release), o => Gen.IUnknownVtbl.Release((void*)o)),
            ("ID3D12Fence.GetCompletedValue", Slot(ref fence, &fence.GetCompletedValue), o => Gen.ID3D12FenceVtbl.GetCompletedValue((void*)o)),
            ("ID3D12Fence.SetEventOnCompletion", Slot(ref fence, &fence.SetEventOnCompletion), o => Gen.ID3D12FenceVtbl.SetEventOnCompletion((void*)o, 0, null)),
            ("ID3D12Fence.Signal", Slot(ref fence, &fence.Signal), o => Gen.ID3D12FenceVtbl.Signal((void*)o, 0)),
            ("ID3D12CommandQueue.ExecuteCommandLists", Slot(ref queue, &queue.ExecuteCommandLists), o => Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists((void*)o, 0, null)),
            ("ID3D12CommandQueue.Signal", Slot(ref queue, &queue.Signal), o => Gen.ID3D12CommandQueueVtbl.Signal((void*)o, null, 0)),
            ("ID3D12CommandQueue.Wait", Slot(ref queue, &queue.Wait), o => Gen.ID3D12CommandQueueVtbl.Wait((void*)o, null, 0)),
            ("IDXGISwapChain.Present", Slot(ref sc, &sc.Present), o => Gen.IDXGISwapChainVtbl.Present((void*)o, 0, 0)),
            ("ID3D12Device.CreateCommandQueue", Slot(ref dev, &dev.CreateCommandQueue), o => Gen.ID3D12DeviceVtbl.CreateCommandQueue((void*)o, null, null, null)),
            ("ID3D12Device.CreateCommandAllocator", Slot(ref dev, &dev.CreateCommandAllocator), o => Gen.ID3D12DeviceVtbl.CreateCommandAllocator((void*)o, default, null, null)),
            ("ID3D12Device.CreateCommandList", Slot(ref dev, &dev.CreateCommandList), o => Gen.ID3D12DeviceVtbl.CreateCommandList((void*)o, 0, default, null, null, null, null)),
            ("ID3D12Device.CheckFeatureSupport", Slot(ref dev, &dev.CheckFeatureSupport), o => Gen.ID3D12DeviceVtbl.CheckFeatureSupport((void*)o, default, null, 0)),
            ("ID3D12Device.CreateFence", Slot(ref dev, &dev.CreateFence), o => Gen.ID3D12DeviceVtbl.CreateFence((void*)o, 0, default, null, null)),
            ("ID3D12CommandAllocator.Reset", Slot(ref alloc, &alloc.Reset), o => Gen.ID3D12CommandAllocatorVtbl.Reset((void*)o)),
            ("ID3D12GraphicsCommandList.Close", Slot(ref list, &list.Close), o => Gen.ID3D12GraphicsCommandListVtbl.Close((void*)o)),
            ("ID3D12GraphicsCommandList.Reset", Slot(ref list, &list.Reset), o => Gen.ID3D12GraphicsCommandListVtbl.Reset((void*)o, null, null)),
            ("ID3D12GraphicsCommandList.Dispatch", Slot(ref list, &list.Dispatch), o => Gen.ID3D12GraphicsCommandListVtbl.Dispatch((void*)o, 0, 0, 0)),
            ("ID3D12GraphicsCommandList.CopyBufferRegion", Slot(ref list, &list.CopyBufferRegion), o => Gen.ID3D12GraphicsCommandListVtbl.CopyBufferRegion((void*)o, null, 0, null, 0, 0)),
            ("ID3D12GraphicsCommandList.CopyTextureRegion", Slot(ref list, &list.CopyTextureRegion), o => Gen.ID3D12GraphicsCommandListVtbl.CopyTextureRegion((void*)o, null, 0, 0, 0, null, null)),
            ("ID3D12GraphicsCommandList.SetPipelineState", Slot(ref list, &list.SetPipelineState), o => Gen.ID3D12GraphicsCommandListVtbl.SetPipelineState((void*)o, null)),
            ("ID3D12GraphicsCommandList.ResourceBarrier", Slot(ref list, &list.ResourceBarrier), o => Gen.ID3D12GraphicsCommandListVtbl.ResourceBarrier((void*)o, 0, null)),
            ("ID3D12GraphicsCommandList.SetDescriptorHeaps", Slot(ref list, &list.SetDescriptorHeaps), o => Gen.ID3D12GraphicsCommandListVtbl.SetDescriptorHeaps((void*)o, 0, null)),
            ("ID3D12GraphicsCommandList.SetComputeRootSignature", Slot(ref list, &list.SetComputeRootSignature), o => Gen.ID3D12GraphicsCommandListVtbl.SetComputeRootSignature((void*)o, null)),
            ("ID3D12GraphicsCommandList.SetComputeRootDescriptorTable", Slot(ref list, &list.SetComputeRootDescriptorTable), o => Gen.ID3D12GraphicsCommandListVtbl.SetComputeRootDescriptorTable((void*)o, 0, default)),
            ("ID3D12GraphicsCommandList.SetComputeRoot32BitConstants", Slot(ref list, &list.SetComputeRoot32BitConstants), o => Gen.ID3D12GraphicsCommandListVtbl.SetComputeRoot32BitConstants((void*)o, 0, 0, null, 0)),
            ("ID3D12GraphicsCommandList4.BeginRenderPass", Slot(ref list4, &list4.BeginRenderPass), o => Gen.ID3D12GraphicsCommandList4Vtbl.BeginRenderPass((void*)o, 0, null, null, default)),
            ("ID3D12GraphicsCommandList4.EndRenderPass", Slot(ref list4, &list4.EndRenderPass), o => Gen.ID3D12GraphicsCommandList4Vtbl.EndRenderPass((void*)o)),
            ("IDXGISwapChain1.Present1", Slot(ref sc1, &sc1.Present1), o => Gen.IDXGISwapChain1Vtbl.Present1((void*)o, 0, 0, null)),
        ];

        foreach (var (name, slot, invoke) in cases)
        {
            Assert.InRange(slot, 1, FakeVtblSlots - 1);
            Assert.True(DispatchesThrough(slot, invoke), $"{name}: the generated binding does not dispatch through TerraFX's slot {slot}");
        }
    }

    [Fact]
    public void GeneratedIidsMatchTheInterfaceGuids()
    {
        Assert.Equal(typeof(IUnknown).GUID, Gen.IUnknownVtbl.IID);
        Assert.Equal(typeof(ID3D12Fence).GUID, Gen.ID3D12FenceVtbl.IID);
        Assert.Equal(typeof(ID3D12CommandQueue).GUID, Gen.ID3D12CommandQueueVtbl.IID);
        Assert.Equal(typeof(IDXGISwapChain).GUID, Gen.IDXGISwapChainVtbl.IID);
        Assert.Equal(typeof(ID3D12Device).GUID, Gen.ID3D12DeviceVtbl.IID);
        Assert.Equal(typeof(ID3D12CommandAllocator).GUID, Gen.ID3D12CommandAllocatorVtbl.IID);
        Assert.Equal(typeof(ID3D12GraphicsCommandList).GUID, Gen.ID3D12GraphicsCommandListVtbl.IID);
        Assert.Equal(typeof(ID3D12GraphicsCommandList4).GUID, Gen.ID3D12GraphicsCommandList4Vtbl.IID);
        Assert.Equal(typeof(IDXGISwapChain1).GUID, Gen.IDXGISwapChain1Vtbl.IID);
    }

    // ── runtime self-check on WARP ───────────────────────────────────────────────────────────────────────────────

    private const uint WaitMs = 10_000;

    private static void WaitFence(void* fence, ulong value)
    {
        if (Gen.ID3D12FenceVtbl.GetCompletedValue(fence) >= value) return;
        HANDLE ev = CreateEventW(null, FALSE, FALSE, null);
        try
        {
            Assert.True(Gen.ID3D12FenceVtbl.SetEventOnCompletion(fence, value, (void*)ev) >= 0);
            WaitForSingleObject(ev, WaitMs);
        }
        finally { CloseHandle(ev); }
        Assert.True(Gen.ID3D12FenceVtbl.GetCompletedValue(fence) >= value, $"fence never reached {value}");
    }

    private static void Release(ref void* p)
    {
        if (p != null) { Gen.IUnknownVtbl.Release(p); p = null; }
    }

    [Fact]
    public void RetainedTileBindingsRunOnAWarpDevice()
    {
        IDXGIFactory4* factory = null;
        IDXGIAdapter* warp = null;
        ID3D12Device* device = null;
        if (CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0) Assert.Skip("DXGI unavailable");
        void* copyQ = null, computeQ = null, directQ = null, fence = null, copyAlloc = null, copyList = null;
        void* directAlloc = null, directList = null, rtvHeap = null, target = null, swap = null;
        try
        {
            if (factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&warp) < 0
                || D3D12CreateDevice((IUnknown*)warp, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device) < 0)
                Assert.Skip("no D3D12 WARP device on this machine");

            // OPTIONS5 → RenderPassesTier (any tier is valid: tier 0 is runtime-emulated).
            Assert.True(D3D12SideQueues.QueryRenderPassesTier(device, out D3D12_RENDER_PASS_TIER tier) >= 0);
            Assert.InRange((int)tier, 0, 2);

            // COPY + COMPUTE side queues sharing one fence: copy signals 1, compute GPU-waits on it then signals 2.
            Assert.True(D3D12SideQueues.CreateQueue(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY, out copyQ) >= 0);
            Assert.True(D3D12SideQueues.CreateQueue(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE, out computeQ) >= 0);
            Assert.True(D3D12SideQueues.CreateFence(device, 0, out fence) >= 0);
            Assert.True(D3D12SideQueues.CreateAllocator(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY, out copyAlloc) >= 0);
            Assert.True(D3D12SideQueues.CreateClosedList(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY, copyAlloc, false, out copyList) >= 0);
            Assert.True(Gen.ID3D12CommandAllocatorVtbl.Reset(copyAlloc) >= 0);
            Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Reset(copyList, copyAlloc, null) >= 0);
            Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Close(copyList) >= 0);
            void* lists = copyList;
            Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists(copyQ, 1, &lists);
            Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(copyQ, fence, 1) >= 0);
            Assert.True(Gen.ID3D12CommandQueueVtbl.Wait(computeQ, fence, 1) >= 0);
            Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(computeQ, fence, 2) >= 0);
            WaitFence(fence, 2);

            // DIRECT list4: one CLEAR → PRESERVE render pass on a real render target.
            Assert.True(D3D12SideQueues.CreateQueue(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, out directQ) >= 0);
            Assert.True(D3D12SideQueues.CreateAllocator(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, out directAlloc) >= 0);
            Assert.True(D3D12SideQueues.CreateClosedList(device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, directAlloc, true, out directList) >= 0);

            D3D12_HEAP_PROPERTIES heap = default;
            heap.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC rd = default;
            rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            rd.Width = 64; rd.Height = 64; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            rd.SampleDesc.Count = 1;
            rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
            void* tex = null;
            Assert.True(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, null, __uuidof<ID3D12Resource>(), &tex) >= 0);
            target = tex;
            D3D12_DESCRIPTOR_HEAP_DESC hd = default;
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            hd.NumDescriptors = 1;
            void* h = null;
            Assert.True(device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), &h) >= 0);
            rtvHeap = h;
            D3D12_CPU_DESCRIPTOR_HANDLE rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
            device->CreateRenderTargetView((ID3D12Resource*)target, null, rtv);

            Assert.True(Gen.ID3D12CommandAllocatorVtbl.Reset(directAlloc) >= 0);
            Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Reset(directList, directAlloc, null) >= 0);
            D3D12_RENDER_PASS_RENDER_TARGET_DESC rt = default;
            rt.cpuDescriptor = rtv;
            rt.BeginningAccess.Type = D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_CLEAR;
            rt.BeginningAccess.Clear.ClearValue.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            rt.EndingAccess.Type = D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_PRESERVE;
            Gen.ID3D12GraphicsCommandList4Vtbl.BeginRenderPass(directList, 1, &rt, null, D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_NONE);
            Gen.ID3D12GraphicsCommandList4Vtbl.EndRenderPass(directList);
            Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Close(directList) >= 0);
            lists = directList;
            Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists(directQ, 1, &lists);
            Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(directQ, fence, 3) >= 0);
            WaitFence(fence, 3);

            // Present1 with DXGI_PRESENT_PARAMETERS on a composition swapchain (no window needed): a full first present,
            // then a dirty-rect present.
            IDXGIFactory2* f2 = null;
            Assert.True(factory->QueryInterface(__uuidof<IDXGIFactory2>(), (void**)&f2) >= 0);
            try
            {
                DXGI_SWAP_CHAIN_DESC1 sd = default;
                sd.Width = 64; sd.Height = 64;
                sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
                sd.SampleDesc.Count = 1;
                sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
                sd.BufferCount = 2;
                sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED;
                IDXGISwapChain1* sc1 = null;
                Assert.True(f2->CreateSwapChainForComposition((IUnknown*)directQ, &sd, null, &sc1) >= 0);
                swap = sc1;
            }
            finally { f2->Release(); }

            DXGI_PRESENT_PARAMETERS full = default;
            Assert.Equal(0, Gen.IDXGISwapChain1Vtbl.Present1(swap, 0, 0, &full));
            RECT dirty = new RECT { left = 8, top = 8, right = 40, bottom = 24 };
            DXGI_PRESENT_PARAMETERS partial = default;
            partial.DirtyRectsCount = 1;
            partial.pDirtyRects = &dirty;
            Assert.Equal(0, Gen.IDXGISwapChain1Vtbl.Present1(swap, 0, 0, &partial));

            // FLIP_DISCARD (the engine's swap effect) refuses PARTIAL presentation outright: after a full present, a
            // Present1 carrying dirty rects is DXGI_ERROR_INVALID_CALL. This is why the composite presents whole frames
            // (docs/plans/scroll-gpu-retained-tiles-implementation.md, P2 status).
            // Drain the direct queue before the swapchain goes: WARP's Present copies (d3d10warp Task_Copy) run on the queue
            // after Present1 returns, and releasing the swapchain under them is the test-host access violation
            // (dxgi!CDXGISwapChain::~CDXGISwapChain freeing the buffers Task_Copy still reads).
            Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(directQ, fence, 4) >= 0); WaitFence(fence, 4);
            Release(ref swap);
            Assert.True(factory->QueryInterface(__uuidof<IDXGIFactory2>(), (void**)&f2) >= 0);
            try
            {
                DXGI_SWAP_CHAIN_DESC1 sd = default;
                sd.Width = 64; sd.Height = 64;
                sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
                sd.SampleDesc.Count = 1;
                sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
                sd.BufferCount = 2;
                sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
                sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED;
                IDXGISwapChain1* sc1 = null;
                Assert.True(f2->CreateSwapChainForComposition((IUnknown*)directQ, &sd, null, &sc1) >= 0);
                swap = sc1;
            }
            finally { f2->Release(); }
            Assert.Equal(0, Gen.IDXGISwapChain1Vtbl.Present1(swap, 0, 0, &full));
            Assert.Equal(unchecked((int)0x887A0001), Gen.IDXGISwapChain1Vtbl.Present1(swap, 0, 0, &partial));

            Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(directQ, fence, 5) >= 0); WaitFence(fence, 5);   // same drain before the finally releases it
            Assert.Equal(0, (int)device->GetDeviceRemovedReason());
        }
        finally
        {
            // Non-throwing drain for the exception path: the swapchain below must never be released under a queued WARP present.
            if (directQ != null && fence != null && Gen.ID3D12CommandQueueVtbl.Signal(directQ, fence, 6) >= 0)
            {
                try { WaitFence(fence, 6); } catch (Xunit.Sdk.XunitException) { }
            }
            Release(ref swap);
            Release(ref directList); Release(ref directAlloc); Release(ref target); Release(ref rtvHeap);
            Release(ref copyList); Release(ref copyAlloc);
            Release(ref fence); Release(ref directQ); Release(ref computeQ); Release(ref copyQ);
            if (device != null) device->Release();
            if (warp != null) warp->Release();
            if (factory != null) factory->Release();
        }
    }
}
