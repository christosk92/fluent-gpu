using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    /// <summary>Isolated native acceptance for the production Store/pool, not just the placement API.</summary>
    internal SmallTextureProbeResult ProbeSmallImagePool()
    {
        if (_primarySwapchain is null || _signalDeviceLostInsteadOfThrow)
            throw new InvalidOperationException("Small-image pool probe requires its own synchronous device.");
        if (!_isUnifiedMemory) return new(false, "adapter is not UMA", [], 0, 0);
        using var store = new ImageTextureStore();
        using var reference = new ImageTextureStore();
        store.Init(_device, unifiedMemory: true); reference.Init(_device, unifiedMemory: true);
        try
        {
            PoolRequire(store.Stage(100, PoolPattern(0), 64, 64) == ImageUploadResult.Accepted, "cold fallback rejected");
            if (store.PlacedHeapBytes == 0)
                return new(false, "placed fallback not selected (atlas supported, small placement unavailable, or no saving)", [], 0, 0);
            PoolRequire(store.PlacedHeapBytes == SmallImageHeapPool.FirstPageBytes, "initial reserve is not 128KiB");
            PoolRequire(!store.TryGetPlacedIdentity(100, out _, out _), "unactivated cold slot was published");
            PoolRequire(store.HasPendingUploads, "cold activation did not request its one submission");
            SmallTextureResetCommands();
            store.FlushUploads(_cmdList, _fenceValue + 1, 0);
            SmallTextureExecute();
            PoolRequire(!store.HasPendingUploads, "submitted activation requests perpetual presentations");
            store.ReclaimCompleted(0);
            PoolRequire(store.Stage(101, PoolPattern(0), 64, 64) == ImageUploadResult.Accepted, "pending fallback rejected");
            PoolRequire(!store.TryGetPlacedIdentity(101, out _, out _), "slot leased before activation completion");
            WaitForGpu(); store.ReclaimCompleted(_fence->GetCompletedValue());

            for (int i = 0; i < 4; i++)
            {
                byte[] pattern = PoolPattern(i);
                PoolRequire(store.Stage(i + 1, pattern, 64, 64) == ImageUploadResult.Accepted, "placed Stage rejected");
                PoolRequire(store.TryGetPlacedIdentity(i + 1, out _, out _), "activated slot not leased");
                PoolRequire(reference.Stage(i + 1, pattern, 64, 64) == ImageUploadResult.Accepted, "reference Stage rejected");
                PoolRequire(!reference.TryGetPlacedIdentity(i + 1, out _, out _), "reference is not committed");
            }
            byte[] first = PoolRender(store, out int width, out int height);
            byte[] expected = PoolRender(reference, out _, out _);
            PoolRequire(first.AsSpan().SequenceEqual(expected), "production placed/committed first pixels differ");

            PoolRequire(store.TryGet(2, out var priorSrv, out _), "prior image missing");
            PoolRequire(store.TryGetPlacedIdentity(2, out nint priorResource, out _), "prior lease missing");
            PoolRequire(store.Stage(2, ReadOnlySpan<byte>.Empty, 64, 64) == ImageUploadResult.Invalid, "invalid replacement accepted");
            PoolRequire(store.TryGet(2, out var afterReject, out _) && afterReject.ptr == priorSrv.ptr, "failed replacement lost old SRV");
            byte[] changed = PoolPattern(1, true);
            PoolRequire(store.Stage(2, changed, 64, 64) == ImageUploadResult.Accepted, "replacement rejected");
            PoolRequire(store.TryGetPlacedIdentity(2, out nint replacement, out _) && replacement != priorResource, "published resource overwritten");
            PoolRequire(store.TryGet(2, out var newSrv, out _) && newSrv.ptr != priorSrv.ptr, "published descriptor overwritten");
            PoolRequire(reference.Stage(2, changed, 64, 64) == ImageUploadResult.Accepted, "reference replacement rejected");
            byte[] updated = PoolRender(store, out _, out _);
            byte[] updatedExpected = PoolRender(reference, out _, out _);
            PoolRequire(updated.AsSpan().SequenceEqual(updatedExpected), "production replacement pixels differ");
            int changedPixels = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    if (first.AsSpan(offset, 4).SequenceEqual(updated.AsSpan(offset, 4))) continue;
                    PoolRequire(x >= 96 && x < 160 && y >= 16 && y < 80, "neighbor pixels changed");
                    changedPixels++;
                }
            PoolRequire(changedPixels > 3000, "replacement did not visibly change");
            WaitForGpu(); store.ReclaimCompleted(_fence->GetCompletedValue());
            ulong bytes = store.PlacedHeapBytes;
            int creates = store.PlacedResourceCreates;
            PoolRequire(store.Stage(66, PoolPattern(0), 64, 64) == ImageUploadResult.Accepted, "warm Stage rejected");
            PoolRequire(store.TryGetPlacedIdentity(66, out nint warmResource, out int generation), "warm image not placed");
            for (int i = 0; i < 32; i++)
            {
                store.Free(66);
                store.ReclaimCompleted(_fence->GetCompletedValue());
                PoolRequire(store.Stage(66, changed, 64, 64) == ImageUploadResult.Accepted, "warm reuse rejected");
                PoolRequire(store.TryGetPlacedIdentity(66, out nint reused, out int next) && reused == warmResource && next != generation,
                    "warm slot identity/generation incorrect");
                generation = next;
            }
            PoolRequire(store.PlacedResourceCreates == creates && store.PlacedHeapBytes == bytes, "warm lease created resources or heaps");
            PoolRequire(bytes <= SmallImageHeapPool.MaxHeapBytes, "reserve cap exceeded");

            // Exercise the second bucket's actual allocation query; unsupported/no-saving must still upload successfully.
            byte[] large = new byte[128 * 128 * 4];
            for (int i = 3; i < large.Length; i += 4) large[i] = 255;
            PoolRequire(store.Stage(128, large, 128, 128) == ImageUploadResult.Accepted, "128px capability fallback rejected");
            store.RefuseNextPlacedWriteForProbe();
            PoolRequire(store.Stage(2, changed, 64, 64) == ImageUploadResult.Accepted, "placed write refusal did not retry committed");
            PoolRequire(!store.TryGetPlacedIdentity(2, out _, out _), "refused bucket reused placed storage");
            byte[] afterRefusal = PoolRender(store, out _, out _);
            PoolRequire(afterRefusal.AsSpan().SequenceEqual(updated), "write-refusal fallback changed image or neighbor pixels");
            Console.Error.WriteLine($"[small-image-pool] heapBytes={store.PlacedHeapBytes} occupiedRequiredBytes={store.PlacedOccupiedBytes} " +
                $"resourceCreates={store.PlacedResourceCreates} warmCreates=0 changedPixels={changedPixels} writeRefusalFallback=exact");
            return new(true, "production activation/fallback, exact pixels, neighbors, fresh SRV, generations, warm reuse and write-refusal fallback pass",
                [first, expected, updated, updatedExpected], width, height);
        }
        finally { WaitForGpu(); } // diagnostic-only; production pool never waits
    }

    private static void PoolRequire(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static byte[] PoolPattern(int identity, bool changed = false)
    {
        byte[] pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                pixels[i] = (byte)(changed ? 240 - x : x * 3);
                pixels[i + 1] = (byte)(changed ? 15 + y : y * 3);
                pixels[i + 2] = (byte)(changed ? 230 : 35 + identity * 45); pixels[i + 3] = 255;
            }
        return pixels;
    }

    private byte[] PoolRender(ImageTextureStore store, out int width, out int height)
    {
        SmallTextureResetCommands();
        store.FlushUploads(_cmdList, _fenceValue + 1, _fence->GetCompletedValue());
        _uploadArena!.BeginFrame(_smallTextureSlot); _imagePipe!.BeginFrame(_smallTextureSlot);
        var back = _f!.Target.BackBuffers[_f!.FrameIndex];
        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _f!.Target.RtvHeap->GetCPUDescriptorHandleForHeapStart(); rtv.ptr += _f!.FrameIndex * _rtvSize;
        _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        float* clear = stackalloc float[4] { 0, 0, 0, 1 }; _cmdList->ClearRenderTargetView(rtv, clear, 0, null);
        D3D12_VIEWPORT viewport = new() { Width = _w, Height = _h, MaxDepth = 1 };
        RECT scissor = new() { right = (int)_w, bottom = (int)_h };
        _cmdList->RSSetViewports(1, &viewport); _cmdList->RSSetScissorRects(1, &scissor);
        _imagePipe.Begin(_cmdList, store.Heap, _w, _h);
        for (int i = 0; i < 4; i++)
        {
            PoolRequire(store.TryGet(i + 1, out var srv, out var uv), "draw image missing");
            var instance = new ImageInstance { PosX = 16 + i * 80, PosY = 16, W = 64, H = 64,
                M11 = 1, M22 = 1, Opacity = 1, CrossFade = 1, UvX = uv.X, UvY = uv.Y, UvW = uv.W, UvH = uv.H, Saturation = 1 };
            _imagePipe.Draw(_cmdList, srv, in instance);
        }
        PoolRequire(_imagePipe.DroppedInstances == 0, "image instance overflow");
        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        SmallTextureExecute();
        byte[] pixels = CaptureBgra(out width, out height); _primarySwapchain!.Present(); return pixels;
    }
}
