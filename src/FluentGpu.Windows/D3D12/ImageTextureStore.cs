using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Resident image textures (media-pipeline.md §4.1). Keyed by the portable <c>imageId</c>. Decoded images arrive at a
/// power-of-two BUCKET size, so:
/// <list type="bullet">
/// <item><b>Atlas</b> (≤128px thumbnails) pack into shared 1024² pages as fixed grid cells — many thumbs share ONE
///   texture/SRV, so a shelf row collapses toward 1–2 binds (the design's "shelf row = 1–2 draws").</item>
/// <item><b>Per-bucket pool</b> (256/512px art) reuses whole bucket textures across evict/re-resident, so steady-state
///   residency does <c>CopyTextureRegion</c> into a pooled texture — <c>CreateTexture</c> only on cold pool growth.</item>
/// </list>
/// Each standalone/pool texture and atlas page gets one shader-visible SRV in a shared CBV_SRV_UAV heap; the
/// <see cref="ImagePipeline"/> binds the SRV via a descriptor table and samples the per-image sub-rect (a half-texel
/// inset, so an image smaller than its cell/texture never bleeds). Texture/cell returns are DEFERRED behind the frame
/// fence (a freed resource an in-flight frame may still sample is released ≥2 frames later).
/// </summary>
internal sealed unsafe class ImageTextureStore : IDisposable
{
    private const int MaxSrv = 4096;    // SRV heap depth (pool textures + atlas pages share it)
    private const int PageSize = 1024;  // atlas page side

    private struct Tex
    {
        public bool Atlas;                       // packed into an atlas page (else owns a pool/standalone texture)
        public ID3D12Resource* Resource;         // own texture (pool/standalone); null when Atlas
        public ID3D12Resource* Upload;           // staging buffer (padded rows), awaiting the copy
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;  // bind handle: own SRV (pool/standalone) or the page's SRV (atlas)
        public int Slot;                         // own SRV slot (pool/standalone); -1 when Atlas
        public int W, H, RowPitch;               // image pixels
        public int TexSize;                      // containing texture side (bucket / exact / PageSize)
        public int Ox, Oy;                       // image origin within that texture (cell origin for atlas; else 0,0)
        public int Bucket;                       // pool bucket (0 = standalone, not pooled)
        public int Page;                         // atlas page index (when Atlas)
        public D3D12_RESOURCE_STATES State;       // actual state of Resource (atlas state lives on AtlasPage)
        public bool NeedsCopy, Live;
    }

    private sealed class AtlasPage
    {
        public ID3D12Resource* Tex;
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public int Slot, Bucket;
        public readonly Stack<(int x, int y)> Free = new();
        public D3D12_RESOURCE_STATES State;
        public int Used;
        public bool Live;
    }

    private struct Pooled
    {
        public ID3D12Resource* Resource;
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public int Slot;
        public D3D12_RESOURCE_STATES State;
    }

    // A deferred resource return (released/recycled once the GPU has fenced past any in-flight frame still using it).
    private struct Retire
    {
        public int Frame, Kind;                  // 0 standalone-release | 1 pool-return | 2 atlas-cell-return | 3 upload-release
        public ID3D12Resource* Upload, Resource; // Upload always released; Resource released for kind 0
        public int Bucket, Slot, Page, CellX, CellY;
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public D3D12_RESOURCE_STATES State;
    }

    private ID3D12Device* _device;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_CPU_DESCRIPTOR_HANDLE _srvCpu0;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu0;
    private uint _srvInc;
    private int _nextSlot, _frame, _descriptorHighWater;
    private readonly Dictionary<int, Tex> _byId = new(64);
    private readonly List<int> _pendingCopies = new(32);
    private readonly Stack<int> _freeSlots = new();
    private readonly List<Retire> _retired = new();
    private readonly List<AtlasPage> _pages = new();
    private readonly Dictionary<int, Stack<Pooled>> _pool = new();   // bucket → free textures
    // Per-bucket FREE-pool cap (audit mem-02): without it the free stacks ratchet to the session-peak in-flight count
    // for each bucket and never release GPU memory. Only two buckets are ever pooled (256/512 art; ≤128 thumbs atlas).
    // The free stack only has to bridge the transient gap between a tile evicting and the NEXT tile re-residencing at
    // the SAME bucket within the 2-frame fence window — a page-flip's worth of same-bucket recycle. 4 free per bucket
    // covers that; a return beyond it is RELEASED (the resource is a GPU texture — surplus must give the memory back),
    // routed through the existing fence-deferred retire path (Kind 0) so its SRV slot is reclaimed and it is freed only
    // after the GPU has fenced past any in-flight use. Tradeoff: the next residency spike past the cap re-creates the
    // texture (one CreateTexture of cold-pool-growth cost) instead of reusing a pooled one — never wrong pixels.
    private const int MaxFreePooledTexturesPerBucket = 4;
    private int _atlasCount, _poolCount;

    public ID3D12DescriptorHeap* Heap => _srvHeap;
    public int DroppedThisRun { get; private set; }
    public int AtlasImages => _atlasCount;
    public int PoolImages => _poolCount;
    public int DescriptorCapacity => MaxSrv;
    public int DescriptorSlotsUsed => _nextSlot - _freeSlots.Count;
    public int DescriptorHighWater => _descriptorHighWater;
    /// <summary>True when decoded pixels are staged but not yet copied to their resident texture (drained by
    /// <see cref="FlushUploads"/> at the top of the next submit). The host must NOT skip that submit, or the texture
    /// stays empty and the image renders white — uploads are throttled, so a deferred one can land on an otherwise
    /// idle frame whose DrawList is unchanged.</summary>
    public bool HasPendingUploads => _pendingCopies.Count > 0 || _retired.Count > 0;

    // ── MemCensus accessors (O(1), or a tiny fixed-bucket sum at census cadence — never per-frame) ──
    /// <summary>Images currently packed into atlas pages — O(1) census (alias of <see cref="AtlasImages"/>).</summary>
    internal int AtlasImageCount => _atlasCount;
    internal int AtlasPageCount
    {
        get { int n = 0; for (int i = 0; i < _pages.Count; i++) if (_pages[i].Tex != null) n++; return n; }
    }
    /// <summary>FREE pooled bucket textures retained for reuse — sum of the per-bucket free stacks (a handful of
    /// buckets; census cadence). Distinct from <see cref="PoolImages"/> (pooled textures currently IN USE).</summary>
    internal int PooledTextureCount
    {
        get { int n = 0; foreach (var stk in _pool.Values) n += stk.Count; return n; }
    }
    /// <summary>Resources awaiting the deferred fence-gated reclaim (the retire list) — O(1) census.</summary>
    internal int RetiredCount => _retired.Count;

    public void Init(ID3D12Device* device)
    {
        _device = device;
        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = MaxSrv;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* heap;
        Check(device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "Image.CreateDescriptorHeap");
        _srvHeap = heap;
        _srvCpu0 = heap->GetCPUDescriptorHandleForHeapStart();
        _srvGpu0 = heap->GetGPUDescriptorHandleForHeapStart();
        _srvInc = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12MemoryDiagnostics.Track(_srvHeap, "Image.SrvHeap", (ulong)MaxSrv * _srvInc);
    }

    private static int BucketFor(int px) => px <= 64 ? 64 : px <= 128 ? 128 : px <= 256 ? 256 : px <= 512 ? 512 : px;

    public bool Has(int id) => _byId.TryGetValue(id, out var t) && (t.Live || t.NeedsCopy);

    /// <summary>The resolved SRV + the per-image sub-rect UV (origin+size in 0..1, half-texel inset) for the pipeline.</summary>
    public bool TryGet(int id, out D3D12_GPU_DESCRIPTOR_HANDLE srv, out RectF uv)
    {
        if (_byId.TryGetValue(id, out var t) && t.Live)
        {
            srv = t.Srv;
            // Atlas pages and pooled textures are square, but >512px images use an exact-size standalone texture.
            // Normalizing both axes by TexSize (= max(W,H)) on that rectangular path sampled only H/TexSize of the
            // texture vertically (a wide hero showed its empty upper band and pushed the subject to the bottom).
            float invX = 1f / (t.Atlas || t.Bucket != 0 ? t.TexSize : t.W);
            float invY = 1f / (t.Atlas || t.Bucket != 0 ? t.TexSize : t.H);
            uv = new RectF(
                (t.Ox + 0.5f) * invX,
                (t.Oy + 0.5f) * invY,
                MathF.Max(0f, t.W - 1) * invX,
                MathF.Max(0f, t.H - 1) * invY);
            return true;
        }
        srv = default; uv = default; return false;
    }

    /// <summary>Heap/upload-only (NO command list): runs during the host's <c>ImageCache.Pump</c>, before the frame list
    /// opens. Routes the image to the atlas (≤128) or the per-bucket pool, (re)acquiring on a routing/size change, and
    /// copies pixels into a staging buffer with a 256-aligned row pitch. The GPU copy is deferred to <see cref="FlushUploads"/>.</summary>
    public ImageUploadResult Stage(int id, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        if (_device == null || w <= 0 || h <= 0 || pbgra8.Length < (long)w * h * 4)
            return ImageUploadResult.Invalid;
        int bucket = BucketFor(Math.Max(w, h));
        bool wantAtlas = bucket <= 128;
        int rowBytes = w * 4;
        int rowPitch = (rowBytes + 255) & ~255;          // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
        long uploadBytes = (long)rowPitch * h;

        bool had = _byId.TryGetValue(id, out var t);
        // Re-route if this id's prior placement (atlas vs pool, or a different bucket) no longer fits the new pixels.
        bool reroute = had && (t.Atlas != wantAtlas || t.Bucket != (wantAtlas ? bucket : (bucket <= 512 ? bucket : 0)));
        // Acquire the replacement before retiring the old placement. A rejected full-res upload must not destroy an
        // already-resident blur-hash texture.
        Tex prior = reroute ? t : default;
        if (reroute) { t = default; had = false; }

        if (!had)
        {
            if (wantAtlas)
            {
                if (!AcquireCell(bucket, out int page, out int cx, out int cy)) return RejectCapacity();
                t.Atlas = true; t.Page = page; t.Slot = -1; t.Bucket = bucket;
                t.Srv = _pages[page].Srv; t.TexSize = PageSize; t.Ox = cx; t.Oy = cy;
                _atlasCount++;
            }
            else if (bucket <= 512)
            {
                if (!AcquirePooled(bucket, out var pt)) return RejectCapacity();
                t.Atlas = false; t.Resource = pt.Resource; t.Srv = pt.Srv; t.Slot = pt.Slot; t.Bucket = bucket;
                t.TexSize = bucket; t.Ox = 0; t.Oy = 0; t.State = pt.State; t.Live = false;
                _poolCount++;
            }
            else   // > 512: standalone exact-size texture (defensive; the cache buckets to ≤512, so this rarely runs)
            {
                if (!TryAcquireSlot(out int slot)) return RejectCapacity();
                t.Atlas = false; t.Resource = CreateTexture(w, h); t.Slot = slot; t.Bucket = 0;
                t.TexSize = Math.Max(w, h); t.Ox = 0; t.Oy = 0;
                t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST; t.Live = false;
                CreateSrv(t.Resource, slot, out t.Srv);
            }
        }

        if (reroute)
        {
            RetirePlacement(ref prior);
            if (prior.Atlas) _atlasCount--; else if (prior.Bucket > 0) _poolCount--;
        }

        // (Re)allocate the staging upload buffer if it must grow.
        if (t.Upload == null || (long)t.RowPitch * t.H < uploadBytes)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            t.Upload = CreateUpload((uint)uploadBytes, "Image.Upload");
        }
        t.W = w; t.H = h; t.RowPitch = rowPitch;

        void* p; t.Upload->Map(0, null, &p);
        byte* dst = (byte*)p;
        fixed (byte* src = pbgra8)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * rowPitch, rowPitch, rowBytes);
        t.Upload->Unmap(0, null);

        t.NeedsCopy = true;
        _byId[id] = t;
        if (!_pendingCopies.Contains(id)) _pendingCopies.Add(id);
        return ImageUploadResult.Accepted;
    }

    private ImageUploadResult RejectCapacity()
    {
        DroppedThisRun++;
        Diag.Count("d3d12", "imageUploadRejected");
        return ImageUploadResult.ResourceExhausted;
    }

    /// <summary>Evict an image (residency dropped it): return its atlas cell / pool texture for reuse and release its
    /// upload buffer — all DEFERRED behind the frame fence (an in-flight frame may still sample it).</summary>
    public void Free(int id)
    {
        if (_byId.Remove(id, out var t))
        {
            _pendingCopies.Remove(id);
            RetirePlacement(ref t);
            if (t.Atlas) _atlasCount--; else if (t.Bucket > 0) _poolCount--;
        }
    }

    // Queue this Tex's GPU resources for deferred reclaim (cell return / pool return / standalone release + upload).
    private void RetirePlacement(ref Tex t)
    {
        var r = new Retire { Frame = _frame, Upload = t.Upload, State = t.State };
        if (t.Atlas) { r.Kind = 2; r.Page = t.Page; r.CellX = t.Ox; r.CellY = t.Oy; }
        else if (t.Bucket > 0) { r.Kind = 1; r.Bucket = t.Bucket; r.Resource = t.Resource; r.Srv = t.Srv; r.Slot = t.Slot; }
        else { r.Kind = 0; r.Resource = t.Resource; r.Slot = t.Slot; }
        _retired.Add(r);
        t.Resource = null; t.Upload = null;
    }

    /// <summary>Frame top (after the device's WaitForFrame): reclaim resources retired ≥2 frames ago, then record the
    /// deferred copies (atlas cells into their page; pool/standalone into the whole texture) and transition to PSR.</summary>
    public void FlushUploads(ID3D12GraphicsCommandList* cmd)
    {
        _frame++;
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Frame > _frame - 2) continue;   // GPU may still be using it
            var r = _retired[i];
            if (r.Upload != null) { D3D12MemoryDiagnostics.Release(r.Upload, "Image.Upload"); r.Upload->Release(); }
            switch (r.Kind)
            {
                case 0: if (r.Resource != null) { D3D12MemoryDiagnostics.Release(r.Resource, "Image.Texture"); r.Resource->Release(); } _freeSlots.Push(r.Slot); break;
                case 1: ReleasePooled(r.Bucket, new Pooled { Resource = r.Resource, Srv = r.Srv, Slot = r.Slot, State = r.State }); break;
                case 2: ReleaseAtlasCell(r.Page, r.CellX, r.CellY); break;
                case 3: break;
            }
            _retired.RemoveAt(i);
        }

        for (int i = 0; i < _pendingCopies.Count; i++)
        {
            int id = _pendingCopies[i];
            if (!_byId.TryGetValue(id, out var t) || !t.NeedsCopy) continue;

            ID3D12Resource* destTex = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            if (destTex == null) continue;
            D3D12_RESOURCE_STATES state = t.Atlas ? _pages[t.Page].State : t.State;
            if (state != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST)
                Transition(cmd, destTex, state, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);

            D3D12_TEXTURE_COPY_LOCATION dst = default;
            dst.pResource = destTex; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION srcLoc = default;
            srcLoc.pResource = t.Upload; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Width = (uint)t.W;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Height = (uint)t.H;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Depth = 1;
            srcLoc.Anonymous.PlacedFootprint.Footprint.RowPitch = (uint)t.RowPitch;
            cmd->CopyTextureRegion(&dst, (uint)t.Ox, (uint)t.Oy, 0, &srcLoc, null);

            Transition(cmd, destTex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            t.NeedsCopy = false; t.Live = true;
            if (t.Atlas)
            {
                _pages[t.Page].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
                _pages[t.Page].Live = true;
            }
            else t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            if (t.Upload != null)
            {
                _retired.Add(new Retire { Frame = _frame, Kind = 3, Upload = t.Upload });
                t.Upload = null;
            }
            _byId[id] = t;
        }
        _pendingCopies.Clear();
    }

    // ── pool ──────────────────────────────────────────────────────────────────
    private bool AcquirePooled(int bucket, out Pooled pt)
    {
        if (_pool.TryGetValue(bucket, out var stk) && stk.Count > 0) { pt = stk.Pop(); return true; }
        if (!TryAcquireSlot(out int slot)) { pt = default; return false; }
        var res = CreateTexture(bucket, bucket);            // cold pool growth (the only CreateTexture in steady state)
        CreateSrv(res, slot, out var srv);
        pt = new Pooled
        {
            Resource = res, Srv = srv, Slot = slot,
            State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
        };
        return true;
    }

    private void ReleasePooled(int bucket, Pooled pt)
    {
        if (!_pool.TryGetValue(bucket, out var stk)) { stk = new Stack<Pooled>(); _pool[bucket] = stk; }
        if (stk.Count >= MaxFreePooledTexturesPerBucket)
        {
            // Cap reached: the pool is already covering the working set for this bucket. Don't ratchet — give the GPU
            // memory back. Re-queue as a standalone release (Kind 0) on the fence-deferred path: the texture is freed
            // and its SRV slot reclaimed only after the GPU has fenced past any in-flight frame still sampling it.
            _retired.Add(new Retire { Frame = _frame, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
            return;
        }
        stk.Push(pt);
    }

    // ── atlas ─────────────────────────────────────────────────────────────────
    private bool AcquireCell(int bucket, out int page, out int cx, out int cy)
    {
        for (int i = 0; i < _pages.Count; i++)
            if (_pages[i].Tex != null && _pages[i].Bucket == bucket && _pages[i].Free.Count > 0)
            {
                (cx, cy) = _pages[i].Free.Pop(); _pages[i].Used++; page = i; return true;
            }
        for (int i = 0; i < _pages.Count; i++)
            if (_pages[i].Tex == null && _pages[i].Bucket == bucket)
                return CreateAtlasPage(i, bucket, out page, out cx, out cy);
        return CreateAtlasPage(-1, bucket, out page, out cx, out cy);
    }

    private bool CreateAtlasPage(int reuse, int bucket, out int page, out int cx, out int cy)
    {
        if (!TryAcquireSlot(out int slot)) { page = cx = cy = 0; return false; }
        var pg = reuse >= 0 ? _pages[reuse] : new AtlasPage();
        pg.Free.Clear();
        pg.Tex = CreateTexture(PageSize, PageSize);
        pg.Slot = slot;
        pg.Bucket = bucket;
        pg.Used = 0;
        pg.Live = false;
        pg.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
        CreateSrv(pg.Tex, slot, out pg.Srv);
        int per = PageSize / bucket;
        for (int y = per - 1; y >= 0; y--)
            for (int x = per - 1; x >= 0; x--)
                pg.Free.Push((x * bucket, y * bucket));
        if (reuse < 0)
        {
            _pages.Add(pg);
            page = _pages.Count - 1;
        }
        else page = reuse;
        (cx, cy) = pg.Free.Pop();
        pg.Used = 1;
        return true;
    }

    private void ReleaseAtlasCell(int page, int cellX, int cellY)
    {
        if ((uint)page >= (uint)_pages.Count) return;
        var pg = _pages[page];
        if (pg.Tex == null) return;
        pg.Free.Push((cellX, cellY));
        if (pg.Used > 0) pg.Used--;
        if (pg.Used != 0) return;

        D3D12MemoryDiagnostics.Release(pg.Tex, "Image.Texture");
        pg.Tex->Release();
        pg.Tex = null;
        pg.Srv = default;
        pg.Live = false;
        pg.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
        pg.Free.Clear();
        if (pg.Slot >= 0) { _freeSlots.Push(pg.Slot); pg.Slot = -1; }
    }

    private bool TryAcquireSlot(out int slot)
    {
        if (_freeSlots.Count > 0) slot = _freeSlots.Pop();
        else if (_nextSlot < MaxSrv) slot = _nextSlot++;
        else { slot = -1; return false; }
        int used = DescriptorSlotsUsed;
        if (used > _descriptorHighWater) _descriptorHighWater = used;
        return true;
    }

    // ── resource helpers ──────────────────────────────────────────────────────
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
        foreach (var r in _retired)
        {
            if (r.Upload != null) { D3D12MemoryDiagnostics.Release(r.Upload, "Image.Upload"); r.Upload->Release(); }
            if (r.Kind != 2 && r.Resource != null) { D3D12MemoryDiagnostics.Release(r.Resource, "Image.Texture"); r.Resource->Release(); }
        }
        foreach (var stk in _pool.Values)
            foreach (var p in stk)
                if (p.Resource != null) { D3D12MemoryDiagnostics.Release(p.Resource, "Image.Texture"); p.Resource->Release(); }
        foreach (var pg in _pages)
            if (pg.Tex != null) { D3D12MemoryDiagnostics.Release(pg.Tex, "Image.AtlasPage"); pg.Tex->Release(); }
        _byId.Clear(); _retired.Clear(); _pool.Clear(); _pages.Clear();
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Image.SrvHeap"); _srvHeap->Release(); _srvHeap = null; }
    }
}
