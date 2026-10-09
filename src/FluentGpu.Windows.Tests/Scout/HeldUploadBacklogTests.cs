using System;
using System.Threading;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// A burst of covers over DrainImageJobs' per-turn budget: the drain holds the over-budget job, and everything behind it
/// stays in the upload queue, admitted Ready by the UI but in no texture-store map yet. A draw resolved meanwhile (the
/// placeholder of such an id, or a landed preview a same-id replacement behind the hold will supersede) is PROVISIONAL:
/// the retained tile holding it must not count as faithful, or nothing re-rasters it once the pixels land.
/// WARP; skipped where D3D12 is absent.
/// </summary>
public sealed unsafe class HeldUploadBacklogTests
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
    public void DrawsWhileAnUploadIsHeldOverBudget_AreProvisional()
    {
        IDXGIFactory4* factory = null;
        IDXGIAdapter* adapter = null;
        ID3D12Device* device = null;
        try
        {
            if (CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0
                || factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&adapter) < 0
                || D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device) < 0)
            {
                Assert.Skip("no D3D12 WARP device on this machine");
                return;
            }
            using var store = new ImageTextureStore();
            store.Init(device, unifiedMemory: false);

            // A cover whose pixels landed: drawing it is the final picture.
            Assert.Equal(ImageUploadResult.Accepted, store.Stage(7, Solid(32, 32, 0x80), 32, 32));
            store.FlushUploads(null, submitFence: 1, completedFence: 0);
            Until(() => store.ResolveDraw(7, out bool p) && !p, "the cover never landed");

            // The drain held a job over its budget: id 8 (admitted Ready, still queued) draws its placeholder, and the
            // landed id 7 may have a replacement queued behind the hold. Neither raster is the final picture.
            store.UploadBacklogHeld = true;
            Assert.False(store.ResolveDraw(8, out bool provisional));
            Assert.True(provisional);
            Assert.True(store.ResolveDraw(7, out provisional));
            Assert.True(provisional);

            // The backlog is staged: an id nobody queued is a plain placeholder (no endless re-raster), a landed one final.
            store.UploadBacklogHeld = false;
            Assert.False(store.ResolveDraw(8, out provisional));
            Assert.False(provisional);
            Assert.True(store.ResolveDraw(7, out provisional));
            Assert.False(provisional);
        }
        finally
        {
            if (device != null) device->Release();
            if (adapter != null) adapter->Release();
            if (factory != null) factory->Release();
        }
    }
}
