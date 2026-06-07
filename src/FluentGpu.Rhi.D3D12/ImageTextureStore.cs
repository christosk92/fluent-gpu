using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Resident image textures (media-pipeline.md §4.1). Keyed by the portable <c>imageId</c>: <see cref="Stage"/> copies
/// decoded PREMULTIPLIED BGRA8 pixels into an upload buffer (render-thread-owned, the COM-confinement keystone) and
/// <see cref="FlushUploads"/> records the <c>CopyTextureRegion</c> onto the frame command list, transitioning the
/// texture to <c>PIXEL_SHADER_RESOURCE</c>. Each image gets one shader-visible SRV in a shared CBV_SRV_UAV heap; the
/// <see cref="ImagePipeline"/> binds the per-image SRV via a descriptor table. Standalone-texture path (the design's
/// ≥256px route + the cold-growth route for the per-bucket pool); atlas packing for ≤128px thumbs layers on in M3.
/// </summary>
internal sealed unsafe class ImageTextureStore : IDisposable
{
    private const int MaxImages = 256;   // SRV heap depth; the album-art wall / virtualized window stays well under this

    private struct Tex
    {
        public ID3D12Resource* Resource;   // BGRA8_UNORM TEXTURE2D
        public ID3D12Resource* Upload;     // staging upload buffer (padded rows), awaiting the copy
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public int W, H, Slot, RowPitch;
        public bool NeedsCopy, Live;
    }

    private ID3D12Device* _device;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_CPU_DESCRIPTOR_HANDLE _srvCpu0;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu0;
    private uint _srvInc;
    private int _nextSlot;
    private int _frame;
    private readonly Dictionary<int, Tex> _byId = new(64);
    private readonly List<int> _pendingCopies = new(32);
    private readonly Stack<int> _freeSlots = new();                 // SRV heap slots reclaimed from evicted images
    private readonly List<(Tex tex, int frame)> _retired = new();   // freed textures, released after the GPU is fenced past them

    public ID3D12DescriptorHeap* Heap => _srvHeap;
    public int DroppedThisRun { get; private set; }

    public void Init(ID3D12Device* device)
    {
        _device = device;
        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = MaxImages;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* heap;
        Check(device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "Image.CreateDescriptorHeap");
        _srvHeap = heap;
        _srvCpu0 = heap->GetCPUDescriptorHandleForHeapStart();
        _srvGpu0 = heap->GetGPUDescriptorHandleForHeapStart();
        _srvInc = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    }

    public bool Has(int id) => _byId.TryGetValue(id, out var t) && (t.Live || t.NeedsCopy);

    public bool TryGet(int id, out D3D12_GPU_DESCRIPTOR_HANDLE srv, out int w, out int h)
    {
        if (_byId.TryGetValue(id, out var t) && t.Live)
        {
            srv = t.Srv; w = t.W; h = t.H; return true;
        }
        srv = default; w = 0; h = 0; return false;
    }

    /// <summary>Heap/upload-only (NO command list): runs during the host's <c>ImageCache.Pump</c>, before the frame
    /// list opens. Create-or-replace the texture + SRV for <paramref name="id"/> and copy pixels into the staging
    /// buffer with a 256-aligned row pitch. The GPU copy is deferred to <see cref="FlushUploads"/>.</summary>
    public void Stage(int id, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        if (_device == null || w <= 0 || h <= 0) return;
        int rowBytes = w * 4;
        int rowPitch = (rowBytes + 255) & ~255;          // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
        long uploadBytes = (long)rowPitch * h;

        if (!_byId.TryGetValue(id, out var t)) t = default;

        // (Re)allocate the texture if new or the dimensions changed (the prior frame already fenced at present).
        if (t.Resource == null || t.W != w || t.H != h)
        {
            if (t.Resource != null) { D3D12MemoryDiagnostics.Release(t.Resource, "Image.Texture"); t.Resource->Release(); t.Resource = null; }
            if (!_byId.ContainsKey(id))   // brand-new id → allocate an SRV-heap slot (reuse a reclaimed one if available)
            {
                int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : (_nextSlot < MaxImages ? _nextSlot++ : -1);
                if (slot < 0) { DroppedThisRun++; return; }
                t.Slot = slot;
            }
            t.Resource = CreateTexture(w, h);
            t.W = w; t.H = h; t.Live = false;
            CreateSrv(t.Resource, t.Slot, out t.Srv);
        }

        // (Re)allocate the staging upload buffer if it must grow.
        if (t.Upload == null || (long)t.RowPitch * t.H < uploadBytes)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            t.Upload = CreateUpload((uint)uploadBytes, "Image.Upload");
        }
        t.RowPitch = rowPitch;

        void* p; t.Upload->Map(0, null, &p);
        byte* dst = (byte*)p;
        fixed (byte* src = pbgra8)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * rowPitch, rowPitch, rowBytes);
        t.Upload->Unmap(0, null);

        t.NeedsCopy = true;
        _byId[id] = t;
        if (!_pendingCopies.Contains(id)) _pendingCopies.Add(id);
    }

    /// <summary>Evict an image's GPU texture (residency dropped it): reclaim its SRV-heap slot immediately and DEFER the
    /// texture release until the GPU has fenced past any in-flight frame still sampling it (handled in FlushUploads).</summary>
    public void Free(int id)
    {
        if (_byId.Remove(id, out var t))
        {
            _pendingCopies.Remove(id);
            _freeSlots.Push(t.Slot);
            _retired.Add((t, _frame));
        }
    }

    /// <summary>Record the deferred CopyTextureRegion(s) onto the frame list (called at the top of SubmitDrawList,
    /// right after the command list Reset). Also releases textures evicted ≥2 frames ago (now GPU-safe) and advances
    /// the retire clock. Transitions each uploaded texture to PIXEL_SHADER_RESOURCE.</summary>
    public void FlushUploads(ID3D12GraphicsCommandList* cmd)
    {
        _frame++;
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_retired[i].frame <= _frame - 2)   // SubmitDrawList already WaitForFrame'd this back-buffer's prior use → GPU done
            {
                var rt = _retired[i].tex;
                if (rt.Upload != null) { D3D12MemoryDiagnostics.Release(rt.Upload, "Image.Upload"); rt.Upload->Release(); }
                if (rt.Resource != null) { D3D12MemoryDiagnostics.Release(rt.Resource, "Image.Texture"); rt.Resource->Release(); }
                _retired.RemoveAt(i);
            }

        for (int i = 0; i < _pendingCopies.Count; i++)
        {
            int id = _pendingCopies[i];
            if (!_byId.TryGetValue(id, out var t) || !t.NeedsCopy || t.Resource == null) continue;

            if (t.Live) Transition(cmd, t.Resource, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);

            D3D12_TEXTURE_COPY_LOCATION dst = default;
            dst.pResource = t.Resource; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION srcLoc = default;
            srcLoc.pResource = t.Upload; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            srcLoc.Anonymous.PlacedFootprint.Offset = 0;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Width = (uint)t.W;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Height = (uint)t.H;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Depth = 1;
            srcLoc.Anonymous.PlacedFootprint.Footprint.RowPitch = (uint)t.RowPitch;
            cmd->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, null);

            Transition(cmd, t.Resource, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            t.NeedsCopy = false; t.Live = true;
            _byId[id] = t;
        }
        _pendingCopies.Clear();
    }

    private ID3D12Resource* CreateTexture(int w, int h)
    {
        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = (ulong)w; td.Height = (uint)h; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        ID3D12Resource* tex;
        Check(_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex), "Image.CreateTexture");
        D3D12MemoryDiagnostics.Track(tex, $"Image.Texture {w}x{h} BGRA8", (ulong)w * (uint)h * 4);
        return tex;
    }

    private void CreateSrv(ID3D12Resource* tex, int slot, out D3D12_GPU_DESCRIPTOR_HANDLE gpu)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
        sd.Anonymous.Texture2D.MipLevels = 1;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = _srvCpu0; cpu.ptr += (nuint)((ulong)slot * _srvInc);
        _device->CreateShaderResourceView(tex, &sd, cpu);
        gpu = _srvGpu0; gpu.ptr += (ulong)slot * _srvInc;
    }

    private ID3D12Resource* CreateUpload(uint bytes, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Image.CreateUpload");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    private static void Transition(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        cmd->ResourceBarrier(1, &b);
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    public void Dispose()
    {
        foreach (var t in _byId.Values)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            if (t.Resource != null) { D3D12MemoryDiagnostics.Release(t.Resource, "Image.Texture"); t.Resource->Release(); }
        }
        foreach (var (t, _) in _retired)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            if (t.Resource != null) { D3D12MemoryDiagnostics.Release(t.Resource, "Image.Texture"); t.Resource->Release(); }
        }
        _byId.Clear();
        _retired.Clear();
        if (_srvHeap != null) { _srvHeap->Release(); _srvHeap = null; }
    }
}
