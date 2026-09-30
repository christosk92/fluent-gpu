using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    internal sealed record SmallTextureProbeResult(bool Supported, string Detail, byte[][] Frames, int Width, int Height);

    /// <summary>Isolated, synchronous native capability experiment. Never called by image admission or an AppHost.</summary>
    internal SmallTextureProbeResult ProbeSmallTexturePlacement()
    {
        if (_primarySwapchain is null || _signalDeviceLostInsteadOfThrow)
            throw new InvalidOperationException("Small-texture probe requires its own initialized synchronous device.");
        if (!_isUnifiedMemory) return new(false, "adapter is not UMA", [], 0, 0);
        const int count = 4, edge = 64;
        ID3D12Heap* heap = null;
        ID3D12DescriptorHeap* descriptors = null;
        var placed = new ID3D12Resource*[count];
        var committed = new ID3D12Resource*[count];
        string stage = "query";
        bool submitted = false;
        try
        {
            D3D12_RESOURCE_DESC desc = default;
            desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = edge; desc.Height = edge; desc.DepthOrArraySize = 1;
            desc.MipLevels = 1; desc.SampleDesc.Count = 1;
            desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
            desc.Alignment = 4096;
            var small = _device->GetResourceAllocationInfo(0, 1, &desc);
            Console.Error.WriteLine($"[small-texture] query small_size={small.SizeInBytes} small_alignment={small.Alignment}");
            if (small.SizeInBytes == 0 || small.SizeInBytes > long.MaxValue || small.Alignment != 4096)
            {
                desc.Alignment = 0;
                var fallback = _device->GetResourceAllocationInfo(0, 1, &desc);
                return new(false, $"4KiB placement unavailable; fallback size={fallback.SizeInBytes} alignment={fallback.Alignment}", [], 0, 0);
            }
            D3D12_RESOURCE_DESC normal = desc; normal.Alignment = 0;
            var referenceAllocation = _device->GetResourceAllocationInfo(0, 1, &normal);
            if (referenceAllocation.SizeInBytes == 0 || referenceAllocation.SizeInBytes > long.MaxValue)
                return new(false, "default allocation query failed", [], 0, 0);
            ulong stride = checked((small.SizeInBytes + 4095) / 4096 * 4096);
            ulong heapBytes = checked((stride * count + 65535) / 65536 * 65536);
            if (heapBytes > 16 * 1024 * 1024) return new(false, "queried heap exceeds isolated probe bound", [], 0, 0);
            D3D12_HEAP_PROPERTIES properties = default;
            properties.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM;
            properties.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY.D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
            properties.MemoryPoolPreference = D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0;
            D3D12_HEAP_DESC hd = default;
            hd.SizeInBytes = heapBytes; hd.Alignment = 65536; hd.Properties = properties;
            hd.Flags = D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES;
            stage = "CreateHeap";
            ProbeHr(_device->CreateHeap(&hd, __uuidof<ID3D12Heap>(), (void**)&heap), stage);
            for (int i = 0; i < count; i++)
            {
                ulong offset = checked((ulong)i * stride);
                if (offset % small.Alignment != 0 || checked(offset + small.SizeInBytes) > heapBytes)
                    throw new InvalidOperationException("Invalid small-texture placement arithmetic.");
                stage = $"CreatePlacedResource[{i}]";
                ID3D12Resource* resource = null;
                ProbeHr(_device->CreatePlacedResource(heap, offset, &desc,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null,
                    __uuidof<ID3D12Resource>(), (void**)&resource), stage);
                placed[i] = resource;
                stage = $"CreateCommittedResource[{i}]";
                resource = null;
                ProbeHr(_device->CreateCommittedResource(&properties, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                    &normal, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null,
                    __uuidof<ID3D12Resource>(), (void**)&resource), stage);
                committed[i] = resource;
            }
            Console.Error.WriteLine($"[small-texture] allocated count={count} resource_required_each={small.SizeInBytes} " +
                $"resource_alignment={small.Alignment} stride={stride} heap_alignment=65536 heap_bytes={heapBytes} " +
                $"heap_slack={heapBytes - small.SizeInBytes * count} committed_required_total={referenceAllocation.SizeInBytes * count} " +
                "note=heap_and_placed_requirements_are_not_additive;not_working_set");
            stage = "activate";
            SmallTextureResetCommands();
            for (int i = 0; i < count; i++)
            {
                D3D12_RESOURCE_BARRIER barrier = default;
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_ALIASING;
                barrier.Anonymous.Aliasing.pResourceAfter = placed[i];
                _cmdList->ResourceBarrier(1, &barrier);
            }
            submitted = true;
            SmallTextureExecute();
            WaitForGpu(); // activation completed before CPU initializes the opaque-layout resources
            for (int i = 0; i < count; i++)
            {
                stage = $"populate[{i}]";
                SmallTextureWrite(placed[i], i, changed: false);
                SmallTextureWrite(committed[i], i, changed: false);
            }
            stage = "descriptors";
            D3D12_DESCRIPTOR_HEAP_DESC srv = default;
            srv.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
            srv.NumDescriptors = count * 2; srv.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
            ProbeHr(_device->CreateDescriptorHeap(&srv, __uuidof<ID3D12DescriptorHeap>(), (void**)&descriptors), stage);
            uint increment = _device->GetDescriptorHandleIncrementSize(srv.Type);
            var cpu = descriptors->GetCPUDescriptorHandleForHeapStart();
            D3D12_SHADER_RESOURCE_VIEW_DESC view = default;
            view.Format = desc.Format; view.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
            view.Shader4ComponentMapping = 0x1688; view.Anonymous.Texture2D.MipLevels = 1;
            for (int i = 0; i < count * 2; i++)
            {
                _device->CreateShaderResourceView(i < count ? placed[i] : committed[i - count], &view, cpu);
                cpu.ptr += increment;
            }
            stage = "sample-original";
            byte[] first = SmallTextureRender(descriptors, 0, increment, out int width, out int height);
            byte[] reference = SmallTextureRender(descriptors, count, increment, out _, out _);
            if (!first.AsSpan().SequenceEqual(reference)) throw new InvalidOperationException("Placed/committed original pixels differ.");
            WaitForGpu(); // every sampling use retired; no neighbors or descriptors are rewritten
            stage = "update-one";
            SmallTextureWrite(placed[1], 1, changed: true);
            SmallTextureWrite(committed[1], 1, changed: true);
            stage = "sample-updated";
            byte[] updated = SmallTextureRender(descriptors, 0, increment, out _, out _);
            byte[] updatedReference = SmallTextureRender(descriptors, count, increment, out _, out _);
            if (!updated.AsSpan().SequenceEqual(updatedReference)) throw new InvalidOperationException("Placed/committed updated pixels differ.");
            int changedPixels = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int pixel = (y * width + x) * 4;
                    if (first.AsSpan(pixel, 4).SequenceEqual(updated.AsSpan(pixel, 4))) continue;
                    if (x < 96 || x >= 160 || y < 16 || y >= 80)
                        throw new InvalidOperationException($"Updating slot1 changed neighbor/background at {x},{y}.");
                    changedPixels++;
                }
            if (changedPixels < 3000) throw new InvalidOperationException("Updated texture did not visibly change.");
            return new(true, $"exact committed parity; neighbors unchanged; changedPixels={changedPixels}; heap={heapBytes}; requiredEach={small.SizeInBytes}",
                [first, reference, updated, updatedReference], width, height);
        }
        catch (SmallTextureUnsupported ex)
        {
            Console.Error.WriteLine($"[small-texture] UNSUPPORTED stage={stage} {ex.Message}");
            return new(false, $"stage={stage} {ex.Message}", [], 0, 0);
        }
        finally
        {
            if (submitted) WaitForGpu(); // root watchdog owns a genuinely hung diagnostic process
            for (int i = 0; i < count; i++)
            {
                if (placed[i] != null) placed[i]->Release();
                if (committed[i] != null) committed[i]->Release();
            }
            if (descriptors != null) descriptors->Release();
            if (heap != null) heap->Release();
        }
    }

    private sealed class SmallTextureUnsupported(string message) : Exception(message) { }
    private static void ProbeHr(HRESULT result, string stage)
    {
        if ((int)result < 0) throw new SmallTextureUnsupported($"{stage} HRESULT=0x{(uint)result:X8}");
    }

    private static void SmallTextureWrite(ID3D12Resource* resource, int identity, bool changed)
    {
        byte[] pattern = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                pattern[i] = (byte)(changed ? 240 - x : x * 3);
                pattern[i + 1] = (byte)(changed ? 15 + y : y * 3);
                pattern[i + 2] = (byte)(changed ? 230 : 35 + identity * 45);
                pattern[i + 3] = 255;
            }
        D3D12_RANGE noRead = default;
        ProbeHr(resource->Map(0, &noRead, null), "Map(opaque,null)");
        try
        {
            fixed (byte* source = pattern)
                ProbeHr(resource->WriteToSubresource(0, null, source, 256, 16384), "WriteToSubresource");
        }
        finally { resource->Unmap(0, null); }
        byte[] readback = new byte[pattern.Length];
        ProbeHr(resource->Map(0, null, null), "Map(read opaque,null)");
        try
        {
            fixed (byte* destination = readback)
                ProbeHr(resource->ReadFromSubresource(destination, 256, 16384, 0, null), "ReadFromSubresource");
        }
        finally { D3D12_RANGE noWrite = default; resource->Unmap(0, &noWrite); }
        if (!pattern.AsSpan().SequenceEqual(readback)) throw new InvalidOperationException("CPU texture round-trip differs.");
    }

    // Phase 1 (§3.1/§3.3): this synchronous, single-swapchain probe now goes through BeginTargetFrame/EndTargetFrame
    // and the SubmissionRing like the real submit path, just serially (WaitForGpu before every reset — no in-flight
    // overlap to worry about, so a fixed ring slot per call is fine).
    private int _smallTextureSlot;

    private void SmallTextureResetCommands()
    {
        WaitForGpu();
        TargetFrameState f = _primarySwapchain!.Frame;
        BeginTargetFrame(f);
        f.FrameIndex = f.Target.SwapChain->GetCurrentBackBufferIndex();
        _smallTextureSlot = _ring.NextSlot;
        Check(_ring.Allocators[_smallTextureSlot]->Reset(), "SmallTexture.allocator.Reset");
        Check(_cmdList->Reset(_ring.Allocators[_smallTextureSlot], null), "SmallTexture.list.Reset");
        InvalidateCmdState();
    }

    private void SmallTextureExecute()
    {
        Check(_cmdList->Close(), "SmallTexture.Close");
        ID3D12CommandList* list = (ID3D12CommandList*)_cmdList;
        _queue->ExecuteCommandLists(1, &list);
        TargetFrameState f = _f!;
        ulong v = SignalNext();
        f.FenceValues[f.FrameIndex] = v;
        f.LastSubmitFence = v;
        _ring.Stamp(_smallTextureSlot, v, f.Target, 0);
        EndTargetFrame();
    }

    private byte[] SmallTextureRender(ID3D12DescriptorHeap* descriptors, int first, uint increment, out int width, out int height)
    {
        SmallTextureResetCommands();
        _uploadArena!.BeginFrame(_smallTextureSlot);
        _imagePipe!.BeginFrame(_smallTextureSlot);
        var back = _f!.Target.BackBuffers[_f!.FrameIndex];
        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _f!.Target.RtvHeap->GetCPUDescriptorHandleForHeapStart(); rtv.ptr += _f!.FrameIndex * _rtvSize;
        _cmdList->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        float* clear = stackalloc float[4] { 0, 0, 0, 1 };
        _cmdList->ClearRenderTargetView(rtv, clear, 0, null);
        D3D12_VIEWPORT viewport = new() { Width = _w, Height = _h, MaxDepth = 1 };
        RECT scissor = new() { right = (int)_w, bottom = (int)_h };
        _cmdList->RSSetViewports(1, &viewport); _cmdList->RSSetScissorRects(1, &scissor);
        _imagePipe.Begin(_cmdList, descriptors, _w, _h);
        var gpu = descriptors->GetGPUDescriptorHandleForHeapStart(); gpu.ptr += (ulong)first * increment;
        for (int i = 0; i < 4; i++)
        {
            var instance = new ImageInstance { PosX = 16 + i * 80, PosY = 16, W = 64, H = 64,
                M11 = 1, M22 = 1, Opacity = 1, CrossFade = 1, UvW = 1, UvH = 1, Saturation = 1 };
            _imagePipe.Draw(_cmdList, gpu, in instance); gpu.ptr += increment;
        }
        if (_imagePipe.DroppedInstances != 0) throw new InvalidOperationException("Image probe exhausted instance storage.");
        Barrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        SmallTextureExecute();
        byte[] pixels = CaptureBgra(out width, out height);
        _primarySwapchain!.Present();
        return pixels;
    }
}
