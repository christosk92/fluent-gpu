using System.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// <c>--fg heap-probe</c> (<see cref="FluentGpu.Render.Tiles.TileMemory.HeapProbe"/>): what does this driver charge to create and
/// to release a tile texture's memory, and what do a heap and a placed resource cost instead? Once, at device
/// init, on its own allocator / list / fence (nothing of the frame path): 16 tile textures (1024×512 BGRA8 render targets)
/// as committed resources (untouched, then GPU-cleared), as placed resources in one 32 MiB heap, and in a heap created with
/// <c>D3D12_HEAP_FLAG_CREATE_NOT_ZEROED</c>. Prints per-op mean / max µs and the process private bytes around each step.
/// </summary>
public sealed unsafe partial class D3D12Device
{
    private void RunHeapProbe()
    {
        const int N = 16, W = 1024, H = 512;
        ID3D12CommandAllocator* alloc = null;
        ID3D12GraphicsCommandList* list = null;
        ID3D12Fence* fence = null;
        ID3D12DescriptorHeap* rtvs = null;
        HANDLE ev = default;
        ulong fv = 0;
        var res = stackalloc ID3D12Resource*[N];
        try
        {
            ID3D12CommandAllocator* a0; Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof<ID3D12CommandAllocator>(), (void**)&a0), "heap-probe.alloc"); alloc = a0;
            ID3D12GraphicsCommandList* l0; Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, null, __uuidof<ID3D12GraphicsCommandList>(), (void**)&l0), "heap-probe.list"); list = l0;
            Check(list->Close(), "heap-probe.close");
            ID3D12Fence* f0; Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&f0), "heap-probe.fence"); fence = f0;
            ev = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);
            D3D12_DESCRIPTOR_HEAP_DESC rh = default;
            rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV; rh.NumDescriptors = N;
            ID3D12DescriptorHeap* r0; Check(_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&r0), "heap-probe.rtvs"); rtvs = r0;
            uint rtvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

            D3D12_RESOURCE_DESC rd = default;
            rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            rd.Width = W; rd.Height = H; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; rd.SampleDesc.Count = 1;
            rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
            rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
            var info = _device->GetResourceAllocationInfo(0, 1, &rd);
            D3D12_CLEAR_VALUE cv = default; cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;

            D3D12_FEATURE_DATA_D3D12_OPTIONS7 o7 = default;
            int o7hr = _device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS7, &o7, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS7));
            D3D12_FEATURE_DATA_ARCHITECTURE arch = default;
            _ = _device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE, &arch, (uint)sizeof(D3D12_FEATURE_DATA_ARCHITECTURE));
            Console.Error.WriteLine($"[heap-probe] tile {W}x{H} bytes={info.SizeInBytes} align={info.Alignment} uma={arch.UMA} " +
                $"cacheCoherentUma={arch.CacheCoherentUMA} options7hr=0x{o7hr:X8} meshTier={(int)o7.MeshShaderTier} feedbackTier={(int)o7.SamplerFeedbackTier}");

            double tick = 1e6 / Stopwatch.Frequency;
            long Priv() { using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64; }
            void Line(string what, Span<long> t, int n, long privBefore, long privAfter)
            {
                long sum = 0, max = 0;
                for (int i = 0; i < n; i++) { sum += t[i]; if (t[i] > max) max = t[i]; }
                Console.Error.WriteLine($"[heap-probe] {what,-34} n={n,2} mean={sum * tick / n,9:F1}us max={max * tick,9:F1}us " +
                    $"privateMB {privBefore / 1048576.0:F1} -> {privAfter / 1048576.0:F1}");
            }
            D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int i) { var h = rtvs->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)(i * rtvInc); return h; }
            void ClearAll(bool aliasing)
            {
                Check(alloc->Reset(), "heap-probe.alloc.Reset");
                Check(list->Reset(alloc, null), "heap-probe.list.Reset");
                float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
                for (int i = 0; i < N; i++)
                {
                    if (aliasing)
                    {
                        D3D12_RESOURCE_BARRIER ab = default;
                        ab.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_ALIASING;
                        ab.Anonymous.Aliasing.pResourceAfter = res[i];
                        list->ResourceBarrier(1, &ab);
                    }
                    _device->CreateRenderTargetView(res[i], null, Rtv(i));
                    list->ClearRenderTargetView(Rtv(i), zero, 0, null);
                }
                Check(list->Close(), "heap-probe.list.Close");
                ID3D12CommandList* l = (ID3D12CommandList*)list;
                _queue->ExecuteCommandLists(1, &l);
                Check(_queue->Signal(fence, ++fv), "heap-probe.signal");
                Check(fence->SetEventOnCompletion(fv, ev), "heap-probe.event");
                WaitForSingleObject(ev, INFINITE);
            }
            Span<long> t = stackalloc long[N];

            // A. committed, untouched (create → release), then committed GPU-cleared (create → clear → release); twice each.
            for (int round = 0; round < 2; round++)
            {
                for (int touched = 0; touched < 2; touched++)
                {
                    long p0 = Priv();
                    for (int i = 0; i < N; i++)
                    {
                        long s = Stopwatch.GetTimestamp();
                        ID3D12Resource* r;
                        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, &cv, __uuidof<ID3D12Resource>(), (void**)&r), "heap-probe.committed");
                        t[i] = Stopwatch.GetTimestamp() - s;
                        res[i] = r;
                    }
                    long p1 = Priv();
                    Line($"committed create r{round}{(touched == 1 ? " (to clear)" : "")}", t, N, p0, p1);
                    if (touched == 1) ClearAll(aliasing: false);
                    long p2 = Priv();
                    for (int i = 0; i < N; i++) { long s = Stopwatch.GetTimestamp(); res[i]->Release(); t[i] = Stopwatch.GetTimestamp() - s; res[i] = null; }
                    Line($"committed release r{round}{(touched == 1 ? " (cleared)" : " (untouched)")}", t, N, p2, Priv());
                }
            }

            // B/C. one heap of 32 MiB, placed tiles, GPU-cleared after an aliasing barrier; with and without CREATE_NOT_ZEROED.
            ulong stride = (info.SizeInBytes + 65535) / 65536 * 65536;
            for (int round = 0; round < 2; round++)
            {
                for (int notZeroed = 0; notZeroed < 2; notZeroed++)
                {
                    string tag = (notZeroed == 1 ? "heap NOT_ZEROED" : "heap") + $" r{round}";
                    D3D12_HEAP_DESC hd = default;
                    hd.SizeInBytes = stride * N; hd.Alignment = 65536; hd.Properties = hp;
                    hd.Flags = D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_RT_DS_TEXTURES
                        | (notZeroed == 1 ? D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_CREATE_NOT_ZEROED : 0);
                    ID3D12Heap* heap = null;
                    long p0 = Priv();
                    long s0 = Stopwatch.GetTimestamp();
                    int hr = _device->CreateHeap(&hd, __uuidof<ID3D12Heap>(), (void**)&heap);
                    t[0] = Stopwatch.GetTimestamp() - s0;
                    if (hr < 0 || heap == null) { Console.Error.WriteLine($"[heap-probe] {tag} CreateHeap failed hr=0x{hr:X8}"); continue; }
                    Line($"{tag} create {hd.SizeInBytes >> 20} MiB", t, 1, p0, Priv());
                    for (int i = 0; i < N; i++)
                    {
                        long s = Stopwatch.GetTimestamp();
                        ID3D12Resource* r;
                        Check(_device->CreatePlacedResource(heap, (ulong)i * stride, &rd, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                            &cv, __uuidof<ID3D12Resource>(), (void**)&r), "heap-probe.placed");
                        t[i] = Stopwatch.GetTimestamp() - s;
                        res[i] = r;
                    }
                    Line($"{tag} placed create", t, N, p0, Priv());
                    ClearAll(aliasing: true);
                    long p2 = Priv();
                    for (int i = 0; i < N; i++) { long s = Stopwatch.GetTimestamp(); res[i]->Release(); t[i] = Stopwatch.GetTimestamp() - s; res[i] = null; }
                    Line($"{tag} placed release (cleared)", t, N, p2, Priv());
                    // re-place on the same memory (what a freed block's next tile costs)
                    for (int i = 0; i < N; i++)
                    {
                        long s = Stopwatch.GetTimestamp();
                        ID3D12Resource* r;
                        Check(_device->CreatePlacedResource(heap, (ulong)i * stride, &rd, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                            &cv, __uuidof<ID3D12Resource>(), (void**)&r), "heap-probe.replaced");
                        t[i] = Stopwatch.GetTimestamp() - s;
                        res[i] = r;
                    }
                    Line($"{tag} placed re-create", t, N, p2, Priv());
                    ClearAll(aliasing: true);
                    for (int i = 0; i < N; i++) { res[i]->Release(); res[i] = null; }
                    long p3 = Priv();
                    s0 = Stopwatch.GetTimestamp();
                    heap->Release();
                    t[0] = Stopwatch.GetTimestamp() - s0;
                    Line($"{tag} release (used)", t, 1, p3, Priv());
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heap-probe] FAIL {ex.Message}");
        }
        finally
        {
            for (int i = 0; i < N; i++) if (res[i] != null) res[i]->Release();
            if (rtvs != null) rtvs->Release();
            if (list != null) list->Release();
            if (alloc != null) alloc->Release();
            if (fence != null) fence->Release();
            if (ev != default) CloseHandle(ev);
        }
    }
}
