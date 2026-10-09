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
/// A replacement still on the copy queue (a BlurHash preview's full-res art, a re-bake) keeps the prior pixels drawable,
/// and a draw of them is PROVISIONAL: the retained tile that holds it must not count as faithful, or it never re-rasters
/// once the new pixels land and the preview stays on screen. WARP; skipped where D3D12 is absent.
/// </summary>
public sealed unsafe class ReplacementInFlightTests
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
    public void PriorPixelsDrawnUnderAPendingReplacement_AreProvisional()
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

            // The BlurHash preview (32×32, staged at request): the placeholder stands in while its copy runs.
            Assert.Equal(ImageUploadResult.Accepted, store.Stage(7, Solid(32, 32, 0x80), 32, 32));
            Assert.False(store.ResolveDraw(7, out bool provisional));
            Assert.True(provisional);
            store.FlushUploads(null, submitFence: 1, completedFence: 0);
            Until(() => store.ResolveDraw(7, out bool p) && !p, "the preview never landed");
            uint preview = store.ContentSerial(7);
            Assert.NotEqual(0u, preview);

            // The full-res art lands under the same id: the preview stays drawable, but it is not the final picture.
            Assert.Equal(ImageUploadResult.Accepted, store.Stage(7, Solid(200, 150, 0xFF), 200, 150));
            Assert.True(store.ResolveDraw(7, out provisional));
            Assert.True(provisional);
            Assert.Equal(preview, store.ContentSerial(7));

            store.FlushUploads(null, submitFence: 2, completedFence: 1);
            Until(() => store.ContentSerial(7) != preview, "the replacement never became drawable");
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
