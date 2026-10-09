using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// A hung GPU must never park the render thread inside an image bake: the bake runs at the top of turns whose frame
/// elides, where no frame-fence watchdog can force the controlled device-lost recovery. The compute queue is stalled
/// behind a GPU-side wait on a fence nothing signals (a hang, as the CPU sees it); once every scratch bank holds an
/// unfinished batch, the next bake must leave its job queued instead of waiting. WARP; skipped where D3D12 is absent.
/// </summary>
public sealed unsafe class BakedBlurHungComputeTests
{
    private const int WaitMs = 10_000;

    private static byte[] Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255; }
        return px;
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

    [Fact]
    public void ABakeWhoseBankIsStillInFlight_DefersTheJobInsteadOfWaiting()
    {
        IDXGIFactory4* factory = null;
        IDXGIAdapter* adapter = null;
        ID3D12Device* device = null;
        ID3D12Fence* gate = null;
        try
        {
            if (CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0
                || factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&adapter) < 0
                || D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device) < 0)
            {
                Assert.Skip("no D3D12 WARP device on this machine");
                return;
            }
            Assert.True(device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&gate) >= 0);
            using var store = new ImageTextureStore();
            store.Init(device, unifiedMemory: false);
            using var baker = new BakedBlurCompositor();
            baker.Init(device);
            store.AttachComputeQueue(baker.ComputeQueue);
            Task<bool>? fifth = null;
            try
            {
                Assert.Equal(ImageUploadResult.Accepted, store.Stage(1, Solid(256, 128, 0xFF), 256, 128));
                store.FlushUploads(null, submitFence: 1, completedFence: 0);
                Until(() => store.IsResident(1), "the source never landed");

                // The hang: every compute batch from here on queues behind a fence value nothing signals.
                Assert.True(baker.ComputeQueue.Queue->Wait(gate, 1) >= 0);

                var queue = new BakedBlurQueue();
                for (int i = 0; i <= UploadQueue.Depth; i++)
                    queue.Enqueue(new BakedBlurQueue.Job(Id: 10 + i, SourceId: 1, OutputW: 256, OutputH: 128, SigmaTexels: 12f, Generation: 1));
                for (int i = 0; i < UploadQueue.Depth; i++)   // one batch per scratch bank, none of them completes
                {
                    Until(() => queue.HasRunnableJob, "the job never became runnable");
                    Assert.True(baker.DrainOne(store, queue));
                }
                Assert.True(baker.ComputeQueue.HasInFlight);

                // Every bank is still in flight: the next turn's bake must return at once, its job kept for later.
                Until(() => queue.HasRunnableJob, "the last job never became runnable");
                fifth = Task.Run(() => baker.DrainOne(store, queue));
                Assert.True(fifth.Wait(3000), "the bake blocked the render thread on the hung compute queue");
                Assert.False(fifth.Result);
                Assert.Equal(1, queue.JobCount);

                // The GPU makes progress again: the deferred job runs on a later turn and lands.
                Assert.True(gate->Signal(1) >= 0);
                Until(() => !baker.ComputeQueue.HasInFlightAnyThread, "the stalled batches never completed");
                Until(() => queue.HasRunnableJob, "the deferred job never became runnable");
                Assert.True(baker.DrainOne(store, queue));
                Assert.Equal(0, queue.JobCount);
                Until(() => store.IsResident(10 + UploadQueue.Depth), "the deferred bake never landed");
            }
            finally
            {
                gate->Signal(1);       // release a bake still parked on the stalled queue (before the fix), so it and Dispose finish
                fifth?.Wait(WaitMs);
            }
        }
        finally
        {
            if (gate != null) gate->Release();
            if (device != null) device->Release();
            if (adapter != null) adapter->Release();
            if (factory != null) factory->Release();
        }
    }
}
