using System;
using System.Threading;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The off-frame upload paths of the retained-tiles plan (§C) on a WARP device: a discrete image upload rides the COPY
/// queue and becomes drawable only once its batch's fence passed (a compare, never a wait), a re-stage keeps the prior
/// pixels drawable until the replacement lands, and an image bake runs on the COMPUTE queue (dual-Kawase) and publishes
/// its derivative behind that queue's fence. The pixels are read back through a DIRECT-queue copy — the textures sit in
/// COMMON for life, so every queue reaches them by implicit promotion. Skipped where D3D12 is absent.
/// </summary>
public sealed unsafe class SideQueueUploadTests
{
    private const int WaitMs = 10_000;

    private sealed class Warp : IDisposable
    {
        public IDXGIFactory4* Factory;
        public IDXGIAdapter* Adapter;
        public ID3D12Device* Device;
        public void* Queue, Allocator, List, Fence;
        public ulong FenceValue;

        public static Warp? Create()
        {
            var w = new Warp();
            IDXGIFactory4* factory = null;
            IDXGIAdapter* adapter = null;
            ID3D12Device* device = null;
            if (CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0) return null;
            w.Factory = factory;
            if (factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&adapter) < 0
                || D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device) < 0)
            {
                w.Adapter = adapter;
                w.Dispose();
                return null;
            }
            w.Adapter = adapter;
            w.Device = device;
            Assert.True(D3D12SideQueues.CreateQueue(w.Device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, out w.Queue) >= 0);
            Assert.True(D3D12SideQueues.CreateAllocator(w.Device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, out w.Allocator) >= 0);
            Assert.True(D3D12SideQueues.CreateClosedList(w.Device, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, w.Allocator, false, out w.List) >= 0);
            Assert.True(D3D12SideQueues.CreateFence(w.Device, 0, out w.Fence) >= 0);
            return w;
        }

        /// <summary>Copy the top-left <paramref name="w"/>×<paramref name="h"/> of <paramref name="tex"/> (4-byte texels) to
        /// the CPU through the direct queue and wait for it.</summary>
        public byte[] Readback(ID3D12Resource* tex, int w, int h)
        {
            D3D12_RESOURCE_DESC td = tex->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp;
            uint rows; ulong rowBytes, total;
            Device->GetCopyableFootprints(&td, 0, 1, 0, &fp, &rows, &rowBytes, &total);
            D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
            D3D12_RESOURCE_DESC rd = default;
            rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
            rd.Width = total; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
            rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            ID3D12Resource* rb;
            Assert.True(Device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&rb) >= 0);
            try
            {
                Assert.True(Gen.ID3D12CommandAllocatorVtbl.Reset(Allocator) >= 0);
                Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Reset(List, Allocator, null) >= 0);
                D3D12_TEXTURE_COPY_LOCATION dst = default;
                dst.pResource = rb; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                dst.Anonymous.PlacedFootprint = fp;
                D3D12_TEXTURE_COPY_LOCATION src = default;
                src.pResource = tex; src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                D3D12_BOX box = new() { right = (uint)w, bottom = (uint)h, back = 1 };
                Gen.ID3D12GraphicsCommandListVtbl.CopyTextureRegion(List, &dst, 0, 0, 0, &src, &box);
                Assert.True(Gen.ID3D12GraphicsCommandListVtbl.Close(List) >= 0);
                void* l = List;
                Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists(Queue, 1, &l);
                Assert.True(Gen.ID3D12CommandQueueVtbl.Signal(Queue, Fence, ++FenceValue) >= 0);
                WaitFence(Fence, FenceValue);
                var bytes = new byte[w * h * 4];
                void* p;
                Assert.True(rb->Map(0, null, &p) >= 0);
                for (int y = 0; y < h; y++)
                    new ReadOnlySpan<byte>((byte*)p + (long)y * fp.Footprint.RowPitch, w * 4).CopyTo(bytes.AsSpan(y * w * 4));
                rb->Unmap(0, null);
                return bytes;
            }
            finally { rb->Release(); }
        }

        public void Dispose()
        {
            if (Fence != null) { Gen.IUnknownVtbl.Release(Fence); Fence = null; }
            if (List != null) { Gen.IUnknownVtbl.Release(List); List = null; }
            if (Allocator != null) { Gen.IUnknownVtbl.Release(Allocator); Allocator = null; }
            if (Queue != null) { Gen.IUnknownVtbl.Release(Queue); Queue = null; }
            if (Device != null) { Device->Release(); Device = null; }
            if (Adapter != null) { Adapter->Release(); Adapter = null; }
            if (Factory != null) { Factory->Release(); Factory = null; }
        }
    }

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

    private static void Until(Func<bool> condition, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < WaitMs, what);
            Thread.Sleep(1);
        }
    }

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
        return px;
    }

    [Fact]
    public void DiscreteUploadRidesTheCopyQueue_AndIsDrawableOnlyOnceItsFencePassed()
    {
        using var warp = Warp.Create();
        if (warp is null) { Assert.Skip("no D3D12 WARP device on this machine"); return; }
        using var store = new ImageTextureStore();
        store.Init(warp.Device, unifiedMemory: false);

        var red = Solid(200, 150, 0, 0, 255);   // > 128: a private pooled texture (the 256 bucket)
        Assert.Equal(ImageUploadResult.Accepted, store.Stage(7, red, 200, 150));
        Assert.False(store.IsResident(7));      // staged, not copied: nothing to draw yet
        Assert.True(store.IsInFlight(7));
        Assert.True(store.HasPendingUploads);

        store.FlushUploads(null, submitFence: 1, completedFence: 0);   // the copy batch goes to the COPY queue
        Assert.Equal(1, store.LastFlushCopies);
        Until(() => store.IsResident(7), "the upload never became drawable");
        Assert.False(store.IsInFlight(7));
        Until(() => !store.HasPendingUploads, "the copy batch never reported done");

        Assert.True(store.TryGetBakeSource(7, out var tex, out _));
        byte[] back = warp.Readback(tex, 200, 150);
        Assert.Equal(255, back[2]);                           // R of (0, 0)
        Assert.Equal(0, back[1]);
        Assert.Equal(255, back[(149 * 200 + 199) * 4 + 2]);   // R of the last texel

        // A re-stage never rewrites a published texture: the red pixels stay drawable until the blue ones land.
        Assert.True(store.TryGet(7, out var srvRed, out _));
        var blue = Solid(200, 150, 255, 0, 0);
        Assert.Equal(ImageUploadResult.Accepted, store.Stage(7, blue, 200, 150));
        Assert.True(store.IsResident(7));
        Assert.True(store.TryGet(7, out var srvStill, out _));
        Assert.Equal(srvRed.ptr, srvStill.ptr);
        store.FlushUploads(null, submitFence: 2, completedFence: 1);
        Until(() => store.TryGet(7, out var s, out _) && s.ptr != srvRed.ptr, "the replacement never became drawable");
        Assert.True(store.TryGetBakeSource(7, out var texBlue, out _));
        byte[] back2 = warp.Readback(texBlue, 200, 150);
        Assert.Equal(255, back2[0]);                          // B of (0, 0)
        Assert.Equal(0, back2[2]);
        store.ReclaimCompleted(ulong.MaxValue - 1);           // the red placement retires once nothing can read it
        Assert.False(store.HasRetireBacklog && store.RetiredCount > 1);
    }

    [Fact]
    public void ImageBakeRunsOnTheComputeQueue_AndPublishesTheDerivativeBehindItsFence()
    {
        using var warp = Warp.Create();
        if (warp is null) { Assert.Skip("no D3D12 WARP device on this machine"); return; }
        using var store = new ImageTextureStore();
        store.Init(warp.Device, unifiedMemory: false);
        using var baker = new BakedBlurCompositor();
        baker.Init(warp.Device);
        store.AttachComputeQueue(baker.ComputeQueue);

        // Source: left half white, right half black (256 × 128).
        const int W = 256, H = 128;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = x < W / 2 ? (byte)255 : (byte)0;
                int i = (y * W + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        Assert.Equal(ImageUploadResult.Accepted, store.Stage(1, px, W, H));
        store.FlushUploads(null, submitFence: 1, completedFence: 0);
        Until(() => store.IsResident(1), "the source never landed");

        var queue = new BakedBlurQueue();
        queue.Enqueue(new BakedBlurQueue.Job(Id: 2, SourceId: 1, OutputW: W, OutputH: H, SigmaTexels: 12f, Generation: 1));
        Until(() => queue.HasRunnableJob, "the job never became runnable");
        Assert.True(baker.DrainOne(store, queue));
        Assert.True(queue.TryDequeueResult(out var result));
        Assert.True(result.Ok);
        Until(() => store.IsResident(2), "the derivative never became drawable");

        Assert.True(store.TryGetBakeSource(2, out var derived, out _));
        byte[] back = warp.Readback(derived, result.W, result.H);
        int mid = result.H / 2;
        byte At(int x) => back[(mid * result.W + x) * 4];   // R (RGBA8)
        // Far from the edge the halves keep their values; across it the blur ramps smoothly, white → black.
        Assert.True(At(2) > 230, $"left edge {At(2)}");
        Assert.True(At(result.W - 3) < 25, $"right edge {At(result.W - 3)}");
        int c = result.W / 2;
        Assert.InRange(At(c), 40, 215);
        Assert.True(At(c - result.W / 8) > At(c) && At(c) > At(c + result.W / 8), "the ramp is monotonic across the edge");
    }
}
