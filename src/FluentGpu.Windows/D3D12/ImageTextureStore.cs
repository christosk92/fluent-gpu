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
/// <item><b>Atlas</b> (≤128px thumbnails) pack into shared 1024² pages on a gutter-separated grid — many thumbs share
///   ONE texture/SRV, so a shelf row collapses toward 1–2 binds (the design's "shelf row = 1–2 draws"). Geometry,
///   page growth/retirement, the per-page LIFO free list, the page generations and the census live in the portable
///   <see cref="ImageAtlasPacker"/>; this class owns only the D3D12 resource for each page index it hands out.</item>
/// <item><b>Per-bucket pool</b> (256/512px art) reuses whole bucket textures across evict/re-resident, so steady-state
///   residency does <c>CopyTextureRegion</c> into a pooled texture — <c>CreateTexture</c> only on cold pool growth.</item>
/// </list>
/// Each standalone/pool texture and atlas page gets one shader-visible SRV in a shared CBV_SRV_UAV heap; the
/// <see cref="ImagePipeline"/> binds the SRV via a descriptor table and samples the per-image sub-rect (a half-texel
/// inset, so an image smaller than its cell/texture never bleeds). Texture/cell returns are DEFERRED behind the frame
/// fence (a freed resource is reusable only once its last submission's actual GPU fence completes).
/// <para><b>Two page flavours, one packer.</b> Discrete pages are DEFAULT-heap textures written by
/// <c>CopyTextureRegion</c> out of a staging buffer (barriered <c>… → COPY_DEST → PSR</c> per flush). UMA pages are
/// <c>ROW_MAJOR</c> textures on a <c>CUSTOM</c>(L0/WRITE_BACK) heap, mapped once at creation and written by a plain
/// row-by-row memcpy, sampled straight out of <c>COMMON</c> by implicit promotion: no staging buffer, no
/// <c>CopyTextureRegion</c>, and never a <c>COPY_DEST</c> transition — the barrier the Adreno UMD mishandles
/// (adreno-hang-fixes.md M1). <see cref="ImageAtlasPacker.RequiresCopyDestTransition"/> is that posture as a value.</para>
/// </summary>
internal sealed unsafe class ImageTextureStore : IDisposable
{
    private const int MaxSrv = 4096;    // SRV heap depth (pool textures + atlas pages share it)
    private const int PageSize = 1024;  // atlas page side
    // D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT. Every CreateCommittedResource texture is rounded up to this, which is
    // the whole reason small thumbnails must pack: a 64² BGRA8 thumb is 16 KiB of pixels in a 64 KiB commit (4× waste,
    // and one resident driver resource per thumbnail). Used to report the HONEST committed size to the census.
    private const ulong PlacementAlignment = 65536;

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
        public int Cell, PageGen;                // atlas grid slot + the page generation it was acquired at
        public D3D12_RESOURCE_STATES State;       // actual state of Resource (atlas state lives on AtlasPage)
        public bool NeedsCopy, Live;
    }

    private sealed class AtlasPage
    {
        public ID3D12Resource* Tex;
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public int Slot = -1;
        public D3D12_RESOURCE_STATES State;
        public bool Live;
        // ── CPU-written (UMA) pages only ──
        // Mapped once at creation and NEVER unmapped for the resource's life: this is the UploadArena posture applied to
        // a texture. Legal because the page is ROW_MAJOR on a CUSTOM(L0/WRITE_BACK) heap — Map on a texture requires a
        // ROW_MAJOR layout, and ROW_MAJOR is also what makes a per-cell write byte-disjoint from every other cell (no
        // driver swizzle, no UBWC metadata shared across the surface). RowPitch comes from GetCopyableFootprints, never
        // from an assumed w*4.
        public byte* Mapped;
        public int RowPitch;
        public ulong Bytes;                      // committed size as reported to the census
        public bool CpuWritten;
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
        public ulong Fence;
        public int Kind;                        // 0 standalone-release | 1 pool-return | 2 atlas-cell-return | 3 upload-release
        public ID3D12Resource* Upload, Resource; // Upload always released; Resource released for kind 0
        public int Bucket, Slot;
        public ImageAtlasCell Cell;             // kind 2: the packed cell to hand back to the packer
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public D3D12_RESOURCE_STATES State;
    }

    private struct UploadTransition
    {
        public ID3D12Resource* Resource;
        public D3D12_RESOURCE_STATES Before;
    }

    private ID3D12Device* _device;
    // UMA (integrated/APU/Adreno) upload path — the TRUE D3D12_FEATURE_DATA_ARCHITECTURE.UMA bit (NOT GpuProfile.IsWeak,
    // which also flags WARP). On UMA, decoded pixels are written straight into a CPU-visible CUSTOM-heap (L0/WRITE_BACK)
    // texture — no UPLOAD staging buffer, no CopyTextureRegion, and NO COPY_DEST→PIXEL_SHADER_RESOURCE barrier (the
    // transition the Qualcomm Adreno UMD mishandles → DEVICE_HUNG; adreno-hang-fixes.md M1).
    //
    // Thumbnails (≤128px) go into SHARED CPU-written pages (_packer.Upload == CpuWrite); everything else gets a private
    // pool/standalone texture written with WriteToSubresource. The four invariants that keep the shared page off the hang
    // path — see the ImageAtlasPacker remarks for the long form:
    //   I1  a CPU-written page is created in COMMON, is only ever GPU-READ, and is NEVER a copy destination ⇒ no
    //       ResourceBarrier is ever emitted for it and its tracked State stays COMMON for the resource's whole life.
    //   I2  a cell's texels are written only while the cell is UNPUBLISHED: on a page whose resource was just created
    //       (never submitted) or on a cell handed back through the fence-deferred Kind-2 return, i.e. after the GPU
    //       fenced past every frame that could still have recorded it.
    //   I3  the page is ROW_MAJOR and persistently mapped, so a per-cell write is BYTE-disjoint from every other cell
    //       (linear layout, no driver swizzle, no UBWC metadata shared across the surface). This is what makes I2's
    //       per-CELL scope sound instead of needing a whole-page fence — the same disjoint CPU-write/GPU-read posture
    //       the shared UploadArena already relies on for buffers.
    //   I4  a re-stage of an already-resident id acquires a FRESH placement and retires the old one (the `reroute` rule
    //       below, unchanged on UMA) ⇒ a published cell is immutable; the CPU never rewrites live texels.
    // ROW_MAJOR TEXTURE2D creation is driver-optional, so it is PROBED at the first page create: if the create fails the
    // store sets _umaPagesDisabled and every thumbnail falls back to the proven private-texture path (today's shipped
    // behaviour). The discrete/non-UMA staging path is untouched throughout.
    private bool _uma;
    private bool _umaPagesDisabled;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_CPU_DESCRIPTOR_HANDLE _srvCpu0;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu0;
    private uint _srvInc;
    private int _nextSlot, _descriptorHighWater;
    private ulong _retireFence;
    private readonly Dictionary<int, Tex> _byId = new(64);
    private readonly List<int> _pendingCopies = new(32);
    private readonly Stack<int> _freeSlots = new();
    private readonly List<Retire> _retired = new();
    private readonly List<UploadTransition> _uploadTransitions = new(32);
    private readonly List<AtlasPage> _pages = new();
    // Index-aligned with _pages: the packer hands out the page index, this class creates the resource for it.
    private ImageAtlasPacker _packer = null!;
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

    // Seam Step 1 (ASYNC only): Stage/Free/FlushUploads become render-thread-confined once the host wires the upload
    // queue (AppHost drains it inside the render submit). Armed via MarkRenderConfined; a stray UI-thread Stage/Free then
    // throws under FGGUARD. Inert in default/force-sync (force-sync stages UI-side with no overlap). [Conditional]-erased
    // in Release. NOT set from D3D12Device.MarkRenderConfined (that's submit/present, both modes) — see the seam split.
    private bool _renderConfined;
    public void MarkRenderConfined() => _renderConfined = true;
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertRenderThread() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRender(); }

    // O(1) census mirrors of the former AtlasPageCount/PooledTextureCount ENUMERATIONS. Under async the store mutates on
    // the render thread while DiagResourceTotals (FG_MEM_DIAG) reads the census on another thread: a foreach over _pages
    // (torn Tex pointer) / _pool.Values (structural Dictionary add ⇒ InvalidOperationException) is unsafe. These ints are
    // maintained at every Tex-alloc/free (atlas pages) and free-stack push/pop (pool), read via Volatile.Read. Interlocked
    // is belt-and-suspenders — post-Step-1 both writers are render-side, but it costs ~nothing at these rare sites.
    private int _atlasPageMirror;    // count of atlas pages with a live Tex (was: for-loop counting _pages[i].Tex != null)
    private int _pooledFreeMirror;   // sum of the per-bucket free-pool stack depths (was: foreach _pool.Values .Count)
    private long _atlasPageBytesMirror;  // Σ committed bytes of the live atlas pages (the O(pages) atlas byte line)

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
    internal int AtlasPageCount => System.Threading.Volatile.Read(ref _atlasPageMirror);
    /// <summary>Resident GPU bytes held by atlas PAGES — the honest atlas byte line, O(pages) and never O(images): a
    /// packed cell is not a resource, so it has no byte line of its own (it is a slice of its page's commit).</summary>
    internal long AtlasPageBytes => System.Threading.Volatile.Read(ref _atlasPageBytesMirror);
    /// <summary>Cells one page holds at each packed bucket (64/128) — the packing capacity behind
    /// <see cref="AtlasPageBytes"/>, for the diagnostics line.</summary>
    internal int AtlasCellsPerPage(int bucket) => _packer?.CellCapacity(bucket) ?? 0;
    /// <summary>False when atlas pages are CPU-written and therefore never barriered (the UMA posture). True on the
    /// discrete staging path, where a page IS a <c>CopyTextureRegion</c> destination.</summary>
    internal bool AtlasPagesUseGpuCopy => _packer?.RequiresCopyDestTransition ?? true;
    /// <summary>FREE pooled bucket textures retained for reuse — sum of the per-bucket free stacks (a handful of
    /// buckets; census cadence). Distinct from <see cref="PoolImages"/> (pooled textures currently IN USE). O(1) mirror
    /// (see <see cref="_pooledFreeMirror"/>) — enumerating <c>_pool</c> would race the render-thread pool mutation under async.</summary>
    internal int PooledTextureCount => System.Threading.Volatile.Read(ref _pooledFreeMirror);
    /// <summary>Resources awaiting the deferred fence-gated reclaim (the retire list) — O(1) census.</summary>
    internal int RetiredCount => _retired.Count;

    public void Init(ID3D12Device* device, bool unifiedMemory = false)
    {
        _device = device;
        _uma = unifiedMemory;
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

        // One packer for both flavours: the geometry, growth and census policy is identical, only the upload posture
        // differs. PageBytes is the page's REAL commit (aligned row pitch × height, rounded to the 64 KiB placement
        // granularity) so the census reports what the driver actually reserved, not the nominal pixel count.
        var pageDesc = DescribeTexture(PageSize, PageSize, _uma);
        _packer = new ImageAtlasPacker(PageSize, (long)CommittedBytes(&pageDesc),
            _uma ? ImageAtlasUpload.CpuWrite : ImageAtlasUpload.GpuCopy);
    }

    private static int BucketFor(int px) => px <= 64 ? 64 : px <= 128 ? 128 : px <= 256 ? 256 : px <= 512 ? 512 : px;

    /// <summary>True when a ≤128px thumbnail should be packed into a shared page rather than owning a texture. On UMA
    /// this also requires the ROW_MAJOR CPU-writable page probe to have succeeded (<see cref="_umaPagesDisabled"/>).</summary>
    private bool WantAtlas(int bucket)
        => bucket <= ImageAtlasPacker.MaxPackedBucket && _packer.CanPack(bucket) && !(_uma && _umaPagesDisabled);

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

    internal bool TryGetBakeSource(int id, out ID3D12Resource* resource, out RectF uv)
    {
        if (TryGet(id, out _, out uv) && _byId.TryGetValue(id, out var t))
        {
            resource = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            return resource != null;
        }
        resource = null;
        return false;
    }

    /// <summary>Copy a render-produced image out of a temporary shader-readable target into the ordinary atlas/pool
    /// residency path. Pool growth may allocate once, but steady derivatives reuse the same placements as decoded art;
    /// no committed output render target is created per bake.</summary>
    internal bool TryAdoptBakedFrom(ID3D12GraphicsCommandList* cmd, int id, ID3D12Resource* source, int w, int h)
    {
        AssertRenderThread();
        if (source == null || w <= 0 || h <= 0) return false;
        int bucket = BucketFor(Math.Max(w, h));
        // A baked derivative arrives as a GPU texture, so adopting it is a CopyTextureRegion — the destination must
        // transition through COPY_DEST. That is precisely the barrier a CPU-written page must never see (I1), so on UMA
        // a baked thumbnail takes the private pool/standalone path instead of a shared page. (A private UMA texture's
        // COMMON→COPY_DEST→PSR pair is the shipped behaviour of this default-off path and is shared with nothing.)
        bool wantAtlas = _packer.Upload == ImageAtlasUpload.GpuCopy && WantAtlas(bucket);
        Tex t = default;
        if (wantAtlas)
        {
            if (!AcquireCell(bucket, out var cell)) return false;
            t.Atlas = true; t.Page = cell.Page; t.Cell = cell.Index; t.PageGen = cell.Generation;
            t.Slot = -1; t.Bucket = bucket;
            t.Srv = _pages[cell.Page].Srv; t.TexSize = PageSize; t.Ox = cell.X; t.Oy = cell.Y;
        }
        else if (bucket <= 512)
        {
            if (!AcquirePooled(bucket, out var pt)) return false;
            t.Resource = pt.Resource; t.Srv = pt.Srv; t.Slot = pt.Slot; t.Bucket = bucket;
            t.TexSize = bucket; t.State = pt.State;
        }
        else
        {
            if (!TryAcquireSlot(out int slot)) return false;
            t.Resource = CreateTexture(w, h);
            if (t.Resource == null) { _freeSlots.Push(slot); return false; }   // device-removed window: soft-fail, never throw
            t.Slot = slot; t.Bucket = 0;
            t.TexSize = Math.Max(w, h); t.State = InitialTexState;   // COMMON on UMA (honest state for the adopt transition)
            CreateSrv(t.Resource, slot, out t.Srv);
        }

        if (_byId.Remove(id, out var old))
        {
            _pendingCopies.Remove(id);
            RetirePlacement(ref old);
            if (old.Atlas) _atlasCount--; else if (old.Bucket > 0) _poolCount--;
        }

        ID3D12Resource* destination = t.Atlas ? _pages[t.Page].Tex : t.Resource;
        D3D12_RESOURCE_STATES destinationState = t.Atlas ? _pages[t.Page].State : t.State;
        Transition(cmd, source, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        if (destinationState != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST)
            Transition(cmd, destination, destinationState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);

        D3D12_TEXTURE_COPY_LOCATION dst = default;
        dst.pResource = destination;
        dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.Anonymous.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION src = default;
        src.pResource = source;
        src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.Anonymous.SubresourceIndex = 0;
        D3D12_BOX box = new() { right = (uint)w, bottom = (uint)h, back = 1 };
        cmd->CopyTextureRegion(&dst, (uint)t.Ox, (uint)t.Oy, 0, &src, &box);

        Transition(cmd, destination, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Transition(cmd, source, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        t.W = w; t.H = h; t.Live = true; t.NeedsCopy = false;
        t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        if (t.Atlas)
        {
            _pages[t.Page].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pages[t.Page].Live = true;
            _atlasCount++;
        }
        else if (t.Bucket > 0) _poolCount++;
        _byId[id] = t;
        return true;
    }

    /// <summary>Heap/upload-only (NO command list): runs during the host's <c>ImageCache.Pump</c>, before the frame list
    /// opens. Routes the image to the atlas (≤128) or the per-bucket pool, (re)acquiring on a routing/size change.
    /// Discrete GPUs copy pixels into a staging buffer with a 256-aligned row pitch; the GPU copy is deferred to
    /// <see cref="FlushUploads"/>. On UMA (adreno-hang-fixes.md M1) the pixels are written straight into a CPU-visible
    /// CUSTOM-heap texture via <c>WriteToSubresource</c> here — no staging buffer, no deferred copy, no barrier.</summary>
    public ImageUploadResult Stage(int id, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        AssertRenderThread();   // seam Step 1: render-confined under async (drained inside SubmitDrawList); UI-staged otherwise
        if (_device == null || w <= 0 || h <= 0 || pbgra8.Length < (long)w * h * 4)
            return ImageUploadResult.Invalid;
        int bucket = BucketFor(Math.Max(w, h));
        // Thumbnails (≤128) pack into shared pages on BOTH paths. Discrete pages are staged+copied; UMA pages are
        // CPU-written through their persistent map (I1–I4 above). WantAtlas also answers false on UMA when the
        // ROW_MAJOR page probe failed, in which case a thumbnail falls back to a private pooled texture.
        bool wantAtlas = WantAtlas(bucket);
        int rowBytes = w * 4;
        int rowPitch = (rowBytes + 255) & ~255;          // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT (discrete staging only)
        long uploadBytes = (long)rowPitch * h;

        bool had = _byId.TryGetValue(id, out var t);
        // Re-route if this id's prior placement (atlas vs pool, or a different bucket) no longer fits the new pixels.
        // On UMA, ALSO force a reroute for any re-stage of a resident id: an in-place WriteToSubresource would race an
        // in-flight frame still sampling that exact texture, so instead acquire a FRESH (fenced) texture and retire the
        // old placement through the normal 2-frame fence path — the same guarantee that makes a pooled reuse safe.
        bool reroute = had && (_uma || t.Atlas != wantAtlas || t.Bucket != (wantAtlas ? bucket : (bucket <= 512 ? bucket : 0)));
        // Acquire the replacement before retiring the old placement. A rejected full-res upload must not destroy an
        // already-resident blur-hash texture.
        Tex prior = reroute ? t : default;
        if (reroute) { t = default; had = false; }

        if (!had)
        {
            // Atlas growth can fail for reasons that are NOT "out of memory for this image": the SRV heap is full, or
            // the UMA ROW_MAJOR page probe just answered no. Fall through to the private pooled texture in that case
            // rather than rejecting the image — a thumbnail that could be resident must not be dropped over a page.
            if (wantAtlas && AcquireCell(bucket, out var cell))
            {
                t.Atlas = true; t.Page = cell.Page; t.Cell = cell.Index; t.PageGen = cell.Generation;
                t.Slot = -1; t.Bucket = bucket;
                t.Srv = _pages[cell.Page].Srv; t.TexSize = PageSize; t.Ox = cell.X; t.Oy = cell.Y;
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
                var res = CreateTexture(w, h);
                if (res == null) { _freeSlots.Push(slot); return RejectDeviceFault(); }
                t.Atlas = false; t.Resource = res; t.Slot = slot; t.Bucket = 0;
                t.TexSize = Math.Max(w, h); t.Ox = 0; t.Oy = 0;
                t.State = InitialTexState; t.Live = false;   // COMMON on UMA (WriteToSubresource path); COPY_DEST discrete
                CreateSrv(t.Resource, slot, out t.Srv);
            }
        }

        if (reroute)
        {
            RetirePlacement(ref prior);
            if (prior.Atlas) _atlasCount--; else if (prior.Bucket > 0) _poolCount--;
        }

        if (_uma)
        {
            // UMA fast path (adreno-hang-fixes.md M1): write the decoded pixels STRAIGHT into the CPU-visible texture.
            // No UPLOAD staging buffer, no CopyTextureRegion, and no COPY_DEST→PIXEL_SHADER_RESOURCE barrier — the texture
            // is sampled directly out of COMMON by implicit state promotion. SrcRowPitch is the UNPADDED source stride
            // (w*4, tightly packed BGRA8 — the same stride the discrete row-copy reads); the 256-aligned rowPitch above is
            // a discrete-staging concern only. This is synchronous CPU work on the same thread as the discrete Stage
            // (render-confined under async / UI-side with no GPU overlap under force-sync), always BEFORE the texture's
            // first GPU use (cold texture) or after it was fence-reacquired (every re-stage reroutes) — never mid-sample.
            if (t.Atlas)
            {
                // Packed thumbnail: a row-by-row memcpy into the page's persistent map. No WriteToSubresource (no driver
                // swizzle to get wrong), no barrier, no staging buffer — and byte-disjoint from every other cell on the
                // page because the page is ROW_MAJOR (I3). The cell is unpublished right now: this page was created a
                // few lines ago, or the cell came back through the fence-gated Kind-2 return (I2), and a re-stage of a
                // resident id always rerouted to a fresh cell above (I4).
                var page = _pages[t.Page];
                if (page.Mapped == null) return ReleaseAfterDeviceFault(id, ref t);
                t.W = w; t.H = h; t.RowPitch = rowBytes; t.Upload = null;
                WriteCell(page, new ImageAtlasCell(t.Page, t.Cell, t.Ox, t.Oy, t.Bucket, t.PageGen), pbgra8, w, h);
                page.Live = true;
                t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;   // page state is COMMON for life (I1)
                t.NeedsCopy = false; t.Live = true;
                _byId[id] = t;
                return ImageUploadResult.Accepted;
            }

            ID3D12Resource* umaTex = t.Resource;   // not packed ⇒ a private pool/standalone texture
            if (umaTex == null) return ReleaseAfterDeviceFault(id, ref t);
            t.W = w; t.H = h; t.RowPitch = rowBytes; t.Upload = null;
            D3D12_BOX box = new() { left = (uint)t.Ox, top = (uint)t.Oy, front = 0, right = (uint)(t.Ox + w), bottom = (uint)(t.Oy + h), back = 1 };
            int hr;
            fixed (byte* src = pbgra8)
                hr = (int)umaTex->WriteToSubresource(0, &box, src, (uint)rowBytes, (uint)((long)rowBytes * h));
            if (hr < 0)
            {
                // Device-removed window: WriteToSubresource fails like the CreateCommittedResource/Map calls do on the
                // discrete path. Soft-fail identically (publish + retire this id's placement, reject) — never throw past
                // the render-thread seam.
                NoteResourceFault("Image.WriteToSubresource");
                return ReleaseAfterDeviceFault(id, ref t);
            }
            t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;   // sampled via implicit promotion; never barriered
            t.NeedsCopy = false; t.Live = true;
            _byId[id] = t;
            return ImageUploadResult.Accepted;   // NOT queued into _pendingCopies — FlushUploads has no copy/barrier to do
        }

        // (Re)allocate the staging upload buffer if it must grow.
        if (t.Upload == null || (long)t.RowPitch * t.H < uploadBytes)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            t.Upload = CreateUpload((uint)uploadBytes, "Image.Upload");
        }
        t.W = w; t.H = h; t.RowPitch = rowPitch;

        // Device-removed window (threading-render-seam.md §9): CreateCommittedResource returns DXGI_ERROR_DEVICE_REMOVED
        // rather than a buffer. Soft-fail — publish the current placement so ReleaseAfterDeviceFault retires exactly what
        // this id owns, then reject. Throwing here would escape the render-thread seam and kill the process.
        if (t.Upload == null) return ReleaseAfterDeviceFault(id, ref t);

        void* p = null;
        // Map is the second device touch that fails on a removed device; an unchecked HRESULT left `p` null and the
        // row copy below wrote through it (an access violation, not even a catchable managed throw).
        if ((int)t.Upload->Map(0, null, &p) < 0 || p == null)
        {
            NoteResourceFault("Image.Upload.Map");
            return ReleaseAfterDeviceFault(id, ref t);
        }
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

    /// <summary>A resource create/map inside <see cref="Stage"/> failed — on a live device that is capacity (a driver OOM),
    /// and in the crash we are hardening it is DXGI_ERROR_DEVICE_REMOVED. Either way the honest answer to the cache is the
    /// SAME transient rejection <see cref="RejectCapacity"/> posts (<c>ResourceExhausted</c> → <c>GpuResourceExhausted</c>,
    /// which <c>ImageCache.ReRealizeAllResident</c> re-requests once the device is rebuilt), so this adds NO enum member.
    /// The arming signal is <see cref="ResourceFaults"/>, counted at the failing call itself.</summary>
    private ImageUploadResult RejectDeviceFault()
    {
        DroppedThisRun++;
        return ImageUploadResult.ResourceExhausted;
    }

    /// <summary>Count of GPU resource creates/maps that FAILED (the device-removed signature — on a removed device every
    /// one of them does). Counted at the failing call, so it covers the cold atlas-page / pool-texture growth paths that
    /// answer through <see cref="RejectCapacity"/> as well as the staging buffer. <c>D3D12Device.DrainImageJobs</c> reads
    /// it as a delta across one drain and calls <c>NoteIfDeviceLost()</c> when it moved.</summary>
    public int ResourceFaults { get; private set; }

    private void NoteResourceFault(string what)
    {
        ResourceFaults++;
        Diag.Count("d3d12", "imageResourceFault");
        if (Diag.Enabled) Diag.Event("d3d12", $"image resource fault: {what} (device removed?)");
    }

    /// <summary>Roll back a half-built placement after a device-fault reject: publish the in-progress <paramref name="t"/>
    /// under <paramref name="id"/> so <see cref="Free"/> retires exactly the resources this id now owns (a rerouted
    /// entry's PRIOR placement was already retired above), then reject. Fence-deferred like every other retire.</summary>
    private ImageUploadResult ReleaseAfterDeviceFault(int id, ref Tex t)
    {
        _byId[id] = t;
        Free(id);
        return RejectDeviceFault();
    }

    /// <summary>Evict an image (residency dropped it): return its atlas cell / pool texture for reuse and release its
    /// upload buffer — all DEFERRED behind the frame fence (an in-flight frame may still sample it).</summary>
    public void Free(int id)
    {
        AssertRenderThread();   // seam Step 1: render-confined under async
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
        var r = new Retire { Fence = _retireFence, Upload = t.Upload, State = t.State };
        if (t.Atlas) { r.Kind = 2; r.Cell = new ImageAtlasCell(t.Page, t.Cell, t.Ox, t.Oy, t.Bucket, t.PageGen); }
        else if (t.Bucket > 0) { r.Kind = 1; r.Bucket = t.Bucket; r.Resource = t.Resource; r.Srv = t.Srv; r.Slot = t.Slot; }
        else { r.Kind = 0; r.Resource = t.Resource; r.Slot = t.Slot; }
        _retired.Add(r);
        t.Resource = null; t.Upload = null;
    }

    /// <summary>Frame top: reclaim only resources whose last possible GPU-use fence completed, then record the
    /// deferred copies (atlas cells into their page; pool/standalone into the whole texture) and transition to PSR.</summary>
    public void FlushUploads(ID3D12GraphicsCommandList* cmd, ulong submitFence, ulong completedFence)
    {
        AssertRenderThread();   // seam Step 1: render-confined under async (always render-side; assert makes it explicit)
        _retireFence = submitFence;
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence > completedFence) continue;
            var r = _retired[i];
            if (r.Upload != null) { D3D12MemoryDiagnostics.Release(r.Upload, "Image.Upload"); r.Upload->Release(); }
            switch (r.Kind)
            {
                case 0: if (r.Resource != null) { D3D12MemoryDiagnostics.Release(r.Resource, "Image.Texture"); r.Resource->Release(); } _freeSlots.Push(r.Slot); break;
                case 1: ReleasePooled(r.Bucket, new Pooled { Resource = r.Resource, Srv = r.Srv, Slot = r.Slot, State = r.State }); break;
                case 2: ReleaseAtlasCell(r.Cell); break;
                case 3: break;
            }
            _retired.RemoveAt(i);
        }

        _uploadTransitions.Clear();
        for (int i = 0; i < _pendingCopies.Count; i++)
        {
            int id = _pendingCopies[i];
            if (!_byId.TryGetValue(id, out var t) || !t.NeedsCopy) continue;
            ID3D12Resource* destTex = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            if (destTex == null) continue;
            AddUploadTransition(destTex, t.Atlas ? _pages[t.Page].State : t.State);
        }
        EmitUploadTransitions(cmd, toCopyDest: true);

        for (int i = 0; i < _pendingCopies.Count; i++)
        {
            int id = _pendingCopies[i];
            if (!_byId.TryGetValue(id, out var t) || !t.NeedsCopy) continue;

            ID3D12Resource* destTex = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            if (destTex == null) continue;

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

            t.NeedsCopy = false; t.Live = true;
            if (t.Atlas)
            {
                _pages[t.Page].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
                _pages[t.Page].Live = true;
            }
            else t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            if (t.Upload != null)
            {
                _retired.Add(new Retire { Fence = _retireFence, Kind = 3, Upload = t.Upload });
                t.Upload = null;
            }
            _byId[id] = t;
        }
        EmitUploadTransitions(cmd, toCopyDest: false);
        _uploadTransitions.Clear();
        _pendingCopies.Clear();
    }

    // ── pool ──────────────────────────────────────────────────────────────────
    private bool AcquirePooled(int bucket, out Pooled pt)
    {
        if (_pool.TryGetValue(bucket, out var stk) && stk.Count > 0) { pt = stk.Pop(); System.Threading.Interlocked.Decrement(ref _pooledFreeMirror); return true; }
        if (!TryAcquireSlot(out int slot)) { pt = default; return false; }
        var res = CreateTexture(bucket, bucket);            // cold pool growth (the only CreateTexture in steady state)
        if (res == null) { _freeSlots.Push(slot); pt = default; return false; }   // device-removed: give the slot back, reject
        CreateSrv(res, slot, out var srv);
        pt = new Pooled
        {
            Resource = res, Srv = srv, Slot = slot,
            State = InitialTexState   // COMMON on UMA (WriteToSubresource, no copy/barrier); COPY_DEST discrete
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
            _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
            return;
        }
        stk.Push(pt);
        System.Threading.Interlocked.Increment(ref _pooledFreeMirror);   // free-pool depth +1 — census mirror
    }

    // ── atlas ─────────────────────────────────────────────────────────────────
    /// <summary>Take a packed cell for <paramref name="bucket"/>, growing the atlas by one page when every existing page
    /// of that bucket is full. All geometry/bookkeeping is the packer's; this only creates the page RESOURCE for the
    /// index it reserved (and hands the reservation back on any failure, so a failed create never leaves a phantom page
    /// in the census).</summary>
    private bool AcquireCell(int bucket, out ImageAtlasCell cell)
    {
        if (_packer.TryAcquire(bucket, out cell)) return true;

        int page = _packer.TryReservePage(bucket);
        if (page < 0) return false;
        if (!TryAcquireSlot(out int srvSlot)) { _packer.AbandonPage(page); return false; }

        bool cpuWritten = _packer.Upload == ImageAtlasUpload.CpuWrite;
        var tex = CreatePageTexture(cpuWritten, out byte* mapped, out int rowPitch, out ulong bytes);
        if (tex == null)
        {
            _freeSlots.Push(srvSlot);
            _packer.AbandonPage(page);
            // A ROW_MAJOR CPU-writable TEXTURE2D is driver-optional. One failed create retires UMA page packing for the
            // whole session — every thumbnail falls back to the shipped private-texture path — instead of re-probing
            // (and re-faulting the census) once per thumbnail. Not a switch: the capability answered.
            if (cpuWritten && !_umaPagesDisabled)
            {
                _umaPagesDisabled = true;
                // Always-on, once per session: a GPU run must be able to say WHICH path it took without a flag or a
                // debugger. (No env switch anywhere — the driver answered, and this records the answer.)
                Diag.Count("d3d12", "imageAtlasUmaUnsupported");
                Console.Error.WriteLine("[d3d12] UMA atlas pages unavailable (ROW_MAJOR CPU-writable TEXTURE2D create failed) — thumbnails fall back to private textures");
            }
            return false;
        }

        while (_pages.Count <= page) _pages.Add(new AtlasPage());
        var pg = _pages[page];
        pg.Tex = tex;
        pg.Slot = srvSlot;
        pg.Mapped = mapped;
        pg.RowPitch = rowPitch;
        pg.Bytes = bytes;
        pg.CpuWritten = cpuWritten;
        pg.Live = false;
        // I1: a CPU-written page is created in COMMON and stays there for its whole life (sampled by implicit
        // promotion, never a copy destination). A discrete page starts in COPY_DEST like every staging destination.
        pg.State = cpuWritten
            ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
            : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
        CreateSrv(tex, srvSlot, out pg.Srv);
        System.Threading.Interlocked.Increment(ref _atlasPageMirror);            // census mirrors: count and bytes are
        System.Threading.Interlocked.Add(ref _atlasPageBytesMirror, (long)bytes); // PER PAGE, never per packed image

        cell = _packer.CommitPage(page);
        return cell.IsValid;
    }

    /// <summary>Hand an evicted cell back. Called ONLY from the fence-gated retire drain, which is what makes invariant
    /// I2 true: the cell's texels cannot be re-written until the GPU has fenced past every frame that could still
    /// sample them. The page resource is released when its last live cell goes.</summary>
    private void ReleaseAtlasCell(in ImageAtlasCell cell)
    {
        _packer.Release(cell, out bool pageBecameEmpty);
        if (!pageBecameEmpty) return;

        int page = cell.Page;
        if ((uint)page >= (uint)_pages.Count) return;
        var pg = _pages[page];
        if (pg.Tex == null) return;

        if (pg.Mapped != null) { pg.Tex->Unmap(0, null); pg.Mapped = null; }
        D3D12MemoryDiagnostics.Release(pg.Tex, "Image.AtlasPage");
        pg.Tex->Release();
        pg.Tex = null;
        System.Threading.Interlocked.Decrement(ref _atlasPageMirror);
        System.Threading.Interlocked.Add(ref _atlasPageBytesMirror, -(long)pg.Bytes);
        pg.Bytes = 0;
        pg.RowPitch = 0;
        pg.CpuWritten = false;
        pg.Srv = default;
        pg.Live = false;
        pg.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        if (pg.Slot >= 0) { _freeSlots.Push(pg.Slot); pg.Slot = -1; }
        _packer.RetirePage(page);   // the slot is reusable at ANY bucket; its generation stays bumped
    }

    /// <summary>Write one decoded thumbnail into its cell on a CPU-written (UMA) page: a plain row-by-row memcpy through
    /// the page's persistent map at the device-reported row pitch. No staging buffer, no <c>CopyTextureRegion</c>, no
    /// barrier (I1) — and byte-disjoint from every other cell because the page is ROW_MAJOR (I3). Safe to write now
    /// because the cell is unpublished: brand-new page, or fence-returned cell (I2).</summary>
    private static void WriteCell(AtlasPage pg, in ImageAtlasCell cell, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        int rowBytes = w * 4;
        byte* dst = pg.Mapped + (long)cell.Y * pg.RowPitch + (long)cell.X * 4;
        fixed (byte* src = pbgra8)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * pg.RowPitch, rowBytes, rowBytes);
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
    // The state a freshly-created image texture is tracked in. Discrete: COPY_DEST (the CopyTextureRegion dest). UMA:
    // COMMON — the CPU-writable texture is populated by WriteToSubresource (not a GPU copy) and promotes to
    // PIXEL_SHADER_RESOURCE implicitly on first sample, so it is never barriered on the upload path. Keeping the tracked
    // state honest also means the (default-off) baked-blur adopt path emits the correct COMMON→COPY_DEST transition on UMA.
    private D3D12_RESOURCE_STATES InitialTexState =>
        _uma ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
             : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;

    /// <summary>The BGRA8 TEXTURE2D desc. <paramref name="rowMajor"/> is for CPU-WRITTEN atlas pages only: <c>Map</c> on
    /// a texture is legal only for a ROW_MAJOR layout, and the linear layout is what makes a per-cell write byte-disjoint
    /// from every other cell on the page (invariant I3). Every other texture keeps UNKNOWN so the driver picks its
    /// optimal (tiled/compressed) layout — including the private UMA textures, which are populated by
    /// <c>WriteToSubresource</c> exactly as they ship today.</summary>
    private static D3D12_RESOURCE_DESC DescribeTexture(int w, int h, bool rowMajor)
    {
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = (ulong)w; td.Height = (uint)h; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Layout = rowMajor
            ? D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR
            : D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        return td;
    }

    /// <summary>What a committed resource for <paramref name="td"/> actually costs: the linear subresource footprint
    /// (256-aligned row pitch × height, from the device rather than an assumed <c>w*4</c>) rounded up to the 64 KiB
    /// placement granularity every <c>CreateCommittedResource</c> is subject to. Tracking <c>w*h*4</c> instead —
    /// what the census used to do — under-reports a 64² thumbnail by 4× and is exactly why `gpu bytes` and
    /// `imageBytes` disagreed by tens of MB on UMA.</summary>
    private ulong CommittedBytes(D3D12_RESOURCE_DESC* td)
    {
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = default;
        uint rows = 0; ulong rowBytes = 0, total = 0;
        _device->GetCopyableFootprints(td, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        if (total == 0) total = td->Width * td->Height * 4;
        return (total + PlacementAlignment - 1) & ~(PlacementAlignment - 1);
    }

    private ID3D12Resource* CreateTexture(int w, int h)
    {
        D3D12_RESOURCE_DESC td = DescribeTexture(w, h, rowMajor: false);
        ID3D12Resource* tex = null;

        if (_uma)
        {
            // UMA (adreno-hang-fixes.md M1): a CPU-writable texture on a HEAP_TYPE_CUSTOM heap (L0 memory pool +
            // WRITE_BACK CPU page property — the only heap on which a TEXTURE2D is CPU-mappable / WriteToSubresource-able;
            // a plain UPLOAD heap CANNOT hold a TEXTURE2D). Created in COMMON: it is only ever GPU-READ (as a shader
            // resource), so it promotes COMMON→PIXEL_SHADER_RESOURCE implicitly on first sample and needs NO barrier — the
            // per-frame COPY_DEST→PSR transition (which the Adreno UMD mishandles) is gone. On a true-UMA adapter these
            // heap props match GetCustomHeapProperties(0, DEFAULT); we set them by hand (no COM round-trip, no ABI quirk).
            D3D12_HEAP_PROPERTIES up = default;
            up.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM;
            up.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY.D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
            up.MemoryPoolPreference = D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0;
            up.CreationNodeMask = 0; up.VisibleNodeMask = 0;   // 0 ≡ single-adapter node 1
            if ((int)_device->CreateCommittedResource(&up, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
            { NoteResourceFault("Image.CreateTexture.Uma"); return null; }
            D3D12MemoryDiagnostics.Track(tex, $"Image.Texture.Uma {w}x{h} BGRA8", CommittedBytes(&td));
            return tex;
        }

        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        // NULL on failure, never a throw: on a removed device (DXGI_ERROR_DEVICE_REMOVED, 0x887A0005) every create here
        // fails, and this runs on the fgpu-render thread INSIDE the image drain — a throw past that seam is unobserved
        // and kills the process. Every caller treats null as "could not admit" (media-pipeline.md §4.1).
        if ((int)_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
        { NoteResourceFault("Image.CreateTexture"); return null; }
        D3D12MemoryDiagnostics.Track(tex, $"Image.Texture {w}x{h} BGRA8", CommittedBytes(&td));
        return tex;
    }

    /// <summary>Create ONE atlas page resource for the index the packer reserved.
    /// <para><b>Discrete:</b> a DEFAULT-heap UNKNOWN-layout texture in COPY_DEST — the <c>CopyTextureRegion</c>
    /// destination, barriered per flush exactly as it ships today.</para>
    /// <para><b>UMA:</b> a ROW_MAJOR texture on a CUSTOM(L0/WRITE_BACK) heap created in COMMON and MAPPED once for the
    /// resource's life. It is only ever GPU-READ, so it promotes COMMON→PIXEL_SHADER_RESOURCE implicitly on first sample
    /// and is NEVER barriered (I1). The page is zero-filled once here so the gutter texels are transparent black rather
    /// than whatever the heap held — deterministic, and it makes any hypothetical bleed visibly nothing instead of
    /// garbage. ROW_MAJOR TEXTURE2D creation is driver-optional: a failure disables UMA page packing for the session and
    /// every thumbnail falls back to the proven private-texture path, so this is a capability probe, not a switch.</para></summary>
    private ID3D12Resource* CreatePageTexture(bool cpuWritten, out byte* mapped, out int rowPitch, out ulong bytes)
    {
        mapped = null; rowPitch = 0; bytes = 0;
        D3D12_RESOURCE_DESC td = DescribeTexture(PageSize, PageSize, rowMajor: cpuWritten);
        ID3D12Resource* tex = null;

        if (!cpuWritten)
        {
            D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
            if ((int)_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
            { NoteResourceFault("Image.CreateAtlasPage"); return null; }
            bytes = CommittedBytes(&td);
            D3D12MemoryDiagnostics.Track(tex, $"Image.AtlasPage {PageSize}x{PageSize} BGRA8", bytes);
            return tex;
        }

        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = default;
        uint rows = 0; ulong rowBytes = 0, total = 0;
        _device->GetCopyableFootprints(&td, 0, 1, 0, &fp, &rows, &rowBytes, &total);

        D3D12_HEAP_PROPERTIES up = default;
        up.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM;
        up.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY.D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
        up.MemoryPoolPreference = D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0;
        up.CreationNodeMask = 0; up.VisibleNodeMask = 0;   // 0 ≡ single-adapter node 1
        if ((int)_device->CreateCommittedResource(&up, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
        { NoteResourceFault("Image.CreateAtlasPage.Uma"); return null; }

        void* p = null;
        if ((int)tex->Map(0, null, &p) < 0 || p == null)
        {
            NoteResourceFault("Image.AtlasPage.Map");
            tex->Release();
            return null;
        }

        rowPitch = (int)fp.Footprint.RowPitch;
        if (rowPitch <= 0) rowPitch = PageSize * 4;
        bytes = CommittedBytes(&td);
        mapped = (byte*)p;
        new Span<byte>(mapped, (int)Math.Min((ulong)int.MaxValue, (ulong)rowPitch * PageSize)).Clear();
        D3D12MemoryDiagnostics.Track(tex, $"Image.AtlasPage.Uma {PageSize}x{PageSize} BGRA8", bytes);
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
        ID3D12Resource* res = null;
        // NULL on failure (see CreateTexture): this is the exact call that produced the shipped crash
        // "Image.CreateUpload failed: 0x887A0005" on the fgpu-render thread.
        if ((int)_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res) < 0)
        { NoteResourceFault("Image.CreateUpload"); return null; }
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Enqueue one upload-path <c>… → COPY_DEST → PSR</c> transition pair. A CPU-written atlas page can never
    /// reach here (its pixels never go through <c>_pendingCopies</c>), and the guard makes that structural rather than
    /// incidental: emitting the pair for such a page is the exact barrier the Adreno UMD mishandles (I1).</summary>
    private void AddUploadTransition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before)
    {
        if (IsCpuWrittenPage(resource)) return;
        for (int i = 0; i < _uploadTransitions.Count; i++)
            if (_uploadTransitions[i].Resource == resource) return;
        _uploadTransitions.Add(new UploadTransition { Resource = resource, Before = before });
    }

    private bool IsCpuWrittenPage(ID3D12Resource* resource)
    {
        if (_packer.Upload != ImageAtlasUpload.CpuWrite) return false;   // discrete: no CPU-written page exists
        for (int i = 0; i < _pages.Count; i++)
            if (_pages[i].Tex == resource) return _pages[i].CpuWritten;
        return false;
    }

    private void EmitUploadTransitions(ID3D12GraphicsCommandList* cmd, bool toCopyDest)
    {
        const int Chunk = 32;
        D3D12_RESOURCE_BARRIER* barriers = stackalloc D3D12_RESOURCE_BARRIER[Chunk];
        int n = 0;
        for (int i = 0; i < _uploadTransitions.Count; i++)
        {
            var t = _uploadTransitions[i];
            D3D12_RESOURCE_STATES before = toCopyDest ? t.Before : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
            D3D12_RESOURCE_STATES after = toCopyDest ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            if (before == after) continue;
            barriers[n] = default;
            barriers[n].Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barriers[n].Anonymous.Transition.pResource = t.Resource;
            barriers[n].Anonymous.Transition.StateBefore = before;
            barriers[n].Anonymous.Transition.StateAfter = after;
            barriers[n].Anonymous.Transition.Subresource = 0xFFFFFFFF;
            n++;
            if (n == Chunk) { cmd->ResourceBarrier((uint)n, barriers); n = 0; }
        }
        if (n > 0) cmd->ResourceBarrier((uint)n, barriers);
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
            if (pg.Tex != null)
            {
                if (pg.Mapped != null) { pg.Tex->Unmap(0, null); pg.Mapped = null; }
                D3D12MemoryDiagnostics.Release(pg.Tex, "Image.AtlasPage");
                pg.Tex->Release();
                pg.Tex = null;
            }
        _byId.Clear(); _retired.Clear(); _pool.Clear(); _pages.Clear();
        _packer?.Clear();
        System.Threading.Volatile.Write(ref _atlasPageMirror, 0);
        System.Threading.Volatile.Write(ref _atlasPageBytesMirror, 0);
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Image.SrvHeap"); _srvHeap->Release(); _srvHeap = null; }
    }
}
