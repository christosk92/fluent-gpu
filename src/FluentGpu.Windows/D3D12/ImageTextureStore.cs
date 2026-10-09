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
/// <para><b>Two page flavours, one packer.</b> Discrete pages are DEFAULT-heap simultaneous-access textures written by
/// <c>CopyTextureRegion</c> on the COPY queue out of a staging buffer (never barriered — COMMON for life, the copy queue
/// promotes and decays them). UMA pages are
/// <c>ROW_MAJOR</c> textures on a <c>CUSTOM</c>(L0/WRITE_BACK) heap, mapped once at creation and written by a plain
/// row-by-row memcpy, sampled straight out of <c>COMMON</c> by implicit promotion: no staging buffer, no
/// <c>CopyTextureRegion</c>, and never a <c>COPY_DEST</c> transition — the barrier the Adreno UMD mishandles
/// (adreno-hang-fixes.md M1). <see cref="ImageAtlasPacker.RequiresCopyDestTransition"/> is that posture as a value.</para>
/// <para><b>Upload heap ring (discrete path).</b> The staging buffer a discrete <see cref="Stage"/> writes into is NOT
/// allocated per image. It used to be: every landed cover paid <c>CreateCommittedResource(UPLOAD)</c> + <c>Map</c> +
/// the row memcpy + <c>Unmap</c> on the render thread inside the present turn, and <see cref="FlushCopies"/> retired
/// the heap one flush later, so a scroll that landed 20 covers did 20 heap creates and 20 heap releases on the thread
/// whose Present-to-Present interval IS the vblank measurement (<c>AppHost.NotePresented</c>). A committed-resource
/// create is a kernel round trip (VidMm allocation + page-table update) of 100–500 µs each on a discrete adapter, so
/// image-heavy routes missed 12–18 % of vblanks while the UI frames themselves averaged 0.5 ms. Now the heaps live in
/// a small ring keyed by power-of-two byte bucket (<see cref="MinUploadBucket"/> … <see cref="MaxUploadBucket"/>):
/// <see cref="Stage"/> pops a heap from the request's bucket (creating one only when the bucket is empty) and the
/// fence-deferred retire (<c>Kind 5</c>) hands it back once the GPU has fenced past the copy, so steady-state staging
/// is Map + memcpy + Unmap and nothing else. The FREE ring is capped at <see cref="MaxPooledUploadBytes"/>; a return
/// beyond the cap releases the heap instead (the ring bridges the copy latency, it is not a cache), and a request above
/// the largest bucket still gets a one-off heap released on retire (<c>Kind 3</c>), exactly as before. UMA never
/// allocates a staging heap (pixels go straight into the CPU-visible texture), so the ring is inert there.</para>
/// <para><b>Off-frame uploads (retained tiles §C).</b> The discrete copies ride their own COPY queue
/// (<see cref="UploadQueue"/>): every image texture is created and kept in COMMON, the copy queue promotes it to
/// COPY_DEST and it decays back, the frame's DIRECT queue samples it by implicit promotion — no barrier anywhere. An image
/// is DRAWABLE only once its batch's fence completed (<see cref="UploadFencePolicy"/>, a compare, never a wait); until
/// then the scene draws its placeholder (or the pixels it replaces, kept published until the new ones land) and the host
/// keeps turning (<see cref="HasPendingUploads"/>). A baked derivative is written the same way by the COMPUTE queue
/// (<see cref="BakedBlurCompositor"/>). A resident id is never rewritten in place: a re-stage takes a fresh placement.</para>
/// </summary>
internal sealed unsafe class ImageTextureStore : IDisposable
{
    private const int MaxSrv = 4096;    // SRV heap depth (pool textures + atlas pages share it)
    private int _pageSize = 1024;       // atlas page side (2048 under --fg img-atlas=gpucopy256, whose 256 px cells need the room)
    private bool _atlasGpuCopy;         // UMA experiment (--fg img-atlas=gpucopy): thumbnail pages are copy-queue written, as on a discrete adapter
    private bool _atlasAllocationKnown;

    private struct Tex
    {
        public bool Atlas;                       // packed into an atlas page (else owns a pool/standalone texture)
        public ID3D12Resource* Resource;         // own texture (pool/standalone); null when Atlas
        public ID3D12Resource* Upload;           // staging buffer (padded rows), awaiting the copy
        public long UploadCapacity;              // bytes Upload holds: a ring bucket (≤ MaxUploadBucket) or the exact one-off size
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
        public SmallImageHeapPool.Lease Placed;
        public ulong Fence;                      // the side-queue batch writing it (0 = drawable once published)
        public byte FenceQueue;                  // 0 none | 1 copy (upload) | 2 compute (baked derivative)
        public ulong ComputeReadFence;           // the last compute batch that READS it (a bake source); 0 = none
        public bool Derived;                     // a baked derivative (RGBA8 UAV texture from the derived pool)
        public uint Serial;                      // content identity of this placement's pixels (ContentSerial)
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
        public SmallImageHeapPool.Lease Placed;
        public ID3D12Resource* Resource;
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public int Slot;
        public D3D12_RESOURCE_STATES State;
    }

    // A deferred resource return (released/recycled once the GPU has fenced past any in-flight frame still using it).
    private struct Retire
    {
        public SmallImageHeapPool.Lease Placed; // kind 4: heap-owned placed texture, descriptor returned after fence
        public ulong Fence;
        public int Kind;                        // 0 standalone-release | 1 pool-return | 2 atlas-cell-return | 3 upload-release (one-off heap) | 5 upload-return (ring heap back to its bucket)
        public ID3D12Resource* Upload, Resource; // Upload always recycled via RecycleUpload (ring or release, by capacity); Resource released for kind 0
        public long UploadCapacity;             // bytes Upload holds — decides ring-return vs release in RecycleUpload
        public int Bucket, Slot;
        public ImageAtlasCell Cell;             // kind 2: the packed cell to hand back to the packer
        public D3D12_GPU_DESCRIPTOR_HANDLE Srv;
        public D3D12_RESOURCE_STATES State;
        public ulong SideFence;                 // a side-queue batch that may still write it (0 = none)
        public byte SideQueue;                  // 1 copy | 2 compute
        public ulong ComputeReadFence;          // a compute batch that may still read it (0 = none)
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
    /// <summary>The HRESULT and the stage ("create" / "map") of the probe that retired UMA page packing. Recorded
    /// rather than discarded because the two stages have DIFFERENT fixes — a create refusal means the layout is
    /// unsupported, a map refusal means only the persistent mapping is, and that one can still be written with
    /// WriteToSubresource — and because a probe that logs no reason is a probe nobody can act on.</summary>
    private int _umaPageFaultHr;
    private string? _umaPageFaultStage;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_CPU_DESCRIPTOR_HANDLE _srvCpu0;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu0;
    private uint _srvInc;
    private int _nextSlot, _descriptorHighWater;
    private ulong _retireFence;
    private readonly Dictionary<int, Tex> _byId = new(64);
    private readonly List<int> _pendingCopies = new(32);
    private readonly Stack<int> _freeSlots = new();
    // CANDIDATE FIX (INCIDENT 2026-09, defensive): a slot a Kind-0/Kind-4 retire hands back in ReclaimCompleted below
    // is already fence-correct FOR THE RESOURCE (r.Fence is the last frame that could reference it, and the slot is
    // only reachable once completedFence has passed that exact value — no different from the resource release
    // right next to it). This is EXTRA slack specifically for the descriptor SLOT: a slot index can be read back out
    // of a GPU-visible descriptor table copy made for a frame the recorder built from state that is already a step
    // removed from "the resource's own last use" (opacity/blur/image-pipeline paths stamp descriptor-table copies
    // with their OWN `_fenceValue + 1` reads, not this store's `r.Fence`), so a slot freed the instant its owning
    // resource's fence clears could in principle be handed to a NEW image whose fresh SRV gets written into that
    // table entry while an adjacent, unrelated in-flight frame still holds a copied descriptor-table pointer at the
    // same slot for something else. `FluentGpu.Rhi.SlotQuarantinePolicy.InitialGenerations` (== the engine's own
    // consume-gated quarantine, threading-render-seam.md §5.1) is the standard belt-and-suspenders slack the rest of
    // the engine already applies to exactly this class of "safe by fence, still quarantine the reusable slot a bit
    // longer" hazard (PathRealizationCache, AudioGraphHost — see their own references to this same constant). A slot
    // sits in <see cref="_slotQuarantine"/> for that many additional <see cref="ReclaimCompleted"/> CALLS (each call
    // is one render-thread "generation": either a real submitted frame's FlushUploads or a skip-turn's
    // ReclaimCompletedUploads — both advance forward progress) before it becomes reusable via <see cref="_freeSlots"/>.
    private readonly List<(int Slot, int GenerationsLeft)> _slotQuarantine = new(8);
    private readonly List<Retire> _retired = new();
    // A re-staged id whose new pixels are still on their side queue keeps its PRIOR (drawable) placement published here
    // until they land — a cover never flashes to its placeholder between its blur-hash and its art.
    private readonly List<(int Id, Tex Prior)> _replacing = new(8);
    // Derivative placements reserved by the compute bake, keyed by token, until committed or abandoned.
    private readonly List<(int Token, Tex T)> _reserved = new(2);
    private int _nextReserveToken;
    private UploadQueue? _copyQueue;
    private UploadQueue? _computeQueue;
    // Baked derivatives: RGBA8 unordered-access textures (the compute bake writes them directly), pooled by bucket.
    private readonly Dictionary<int, Stack<Pooled>> _derivedPool = new();
    private const int MaxFreeDerivedPerBucket = 2;
    private readonly List<AtlasPage> _pages = new();
    // Index-aligned with _pages: the packer hands out the page index, this class creates the resource for it.
    private ImageAtlasPacker _packer = null!;
    private readonly Dictionary<int, Stack<Pooled>> _pool = new();   // bucket → free textures
    private SmallImageHeapPool? _smallImages;
    private bool _refuseNextPlacedWriteForProbe;
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

    // ── Upload heap ring (discrete staging only; see the class summary) ──
    // Buckets are the 256-aligned staging footprints of the cache's image buckets: a 128px BGRA8 thumb pads to 64 KB,
    // 256px to 256 KB, 512px to 1 MB; 2 MB/4 MB cover the exact-size standalone heroes up to 1024². Anything larger is a
    // one-off heap (created and released as before). The FREE-heap cap is 16 MB: sixteen 1 MB covers or four 4 MB heroes
    // in flight between a stage and its fenced copy — more than one turn's UploadBytesPerTurn budget (D3D12Device) times
    // the 2-frame fence window — so the ring absorbs the steady state without ratcheting UPLOAD memory to the session
    // peak. A return that would exceed the cap is released outright (Kind 3 semantics), never queued.
    private const int MinUploadBucket = 64 * 1024;
    private const int MaxUploadBucket = 4 * 1024 * 1024;
    private const int UploadBucketCount = 7;                          // 64K, 128K, 256K, 512K, 1M, 2M, 4M
    private const long MaxPooledUploadBytes = 16L * 1024 * 1024;
    private readonly Stack<nint>?[] _uploadRing = new Stack<nint>?[UploadBucketCount];  // bucket index → FREE heaps (ID3D12Resource*); a bucket is null until first return
    private long _uploadRingBytes;                                    // Σ bytes of the FREE heaps held in the ring
    private int _uploadHeapCreates, _uploadHeapReuses;                // cumulative: cold creates vs ring hits (census)

    // Seam Step 1 (ASYNC only): Stage/Free/FlushUploads become render-thread-confined once the host wires the upload
    // queue (AppHost drains it inside the render submit). Armed via MarkRenderConfined; a stray UI-thread Stage/Free then
    // throws under FGGUARD. Inert in default/force-sync (force-sync stages UI-side with no overlap). [Conditional]-erased
    // in Release. NOT set from D3D12Device.MarkRenderConfined (that's submit/present, both modes) — see the seam split.
    private bool _renderConfined;
    public void MarkRenderConfined() => _renderConfined = true;
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertRenderThread() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRender(); }

    // O(1) census mirrors of the former AtlasPageCount/PooledTextureCount ENUMERATIONS. Under async the store mutates on
    // the render thread while DiagResourceTotals (--fg mem) reads the census on another thread: a foreach over _pages
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
    /// idle frame whose DrawList is unchanged. The retire backlog (<see cref="HasRetireBacklog"/>) is deliberately
    /// NOT folded in here: it is fence-only maintenance (<see cref="ReclaimCompleted"/>), reclaimable on an elided
    /// frame via <see cref="FluentGpu.Rhi.IGpuDevice.ReclaimCompletedUploads"/> without opening a command list.</summary>
    public bool HasPendingUploads => _pendingCopies.Count > 0 || (_smallImages?.HasUnsubmittedActivation ?? false)
        || (_copyQueue?.HasInFlightAnyThread ?? false) || (_computeQueue?.HasInFlightAnyThread ?? false);
    /// <summary>True while resources evicted from residency are still waiting on their retire fence (queued by
    /// <see cref="Free"/>/<see cref="RetirePlacement"/>, drained by <see cref="ReclaimCompleted"/>), OR a descriptor
    /// slot has cleared its fence but is still sitting out <see cref="FluentGpu.Rhi.SlotQuarantinePolicy.InitialGenerations"/> generations in
    /// <see cref="_slotQuarantine"/> before <see cref="_freeSlots"/> can reuse it (CANDIDATE FIX, INCIDENT 2026-09 —
    /// see that field's doc). Both drain from the SAME call (<see cref="ReclaimCompleted"/>/
    /// <see cref="FluentGpu.Rhi.IGpuDevice.ReclaimCompletedUploads"/>), so folding them together keeps this flag's
    /// existing meaning — "there is still fence/generation-gated cleanup work outstanding" — accurate for BOTH kinds,
    /// without adding a second flag callers would have to remember to check. Does NOT force a submit on its own — an
    /// elided/idle frame reclaims it for free via ReclaimCompletedUploads instead of waking the render loop.</summary>
    internal bool HasRetireBacklog => _retired.Count > 0 || _slotQuarantine.Count > 0;
    internal ulong PlacedHeapBytes => _smallImages?.HeapBytes ?? 0;
    internal ulong PlacedOccupiedBytes => _smallImages?.OccupiedBytes ?? 0;
    internal int PlacedResourceCreates => _smallImages?.ResourceCreateCount ?? 0;
    // Native fault-injection gate: refuses one CPU write after a successful map, without damaging the real device.
    internal void RefuseNextPlacedWriteForProbe() => _refuseNextPlacedWriteForProbe = true;
    internal bool TryGetPlacedIdentity(int id, out nint resource, out int generation)
    {
        resource = 0; generation = 0;
        if (!_byId.TryGetValue(id, out var t) || !t.Placed.IsValid) return false;
        resource = (nint)t.Resource; generation = t.Placed.Generation; return true;
    }

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
    /// <summary>Bytes of FREE staging heaps held in the upload ring (≤ <see cref="MaxPooledUploadBytes"/>) — O(1) census.</summary>
    internal long UploadRingBytes => _uploadRingBytes;
    /// <summary>Cumulative cold <c>CreateCommittedResource(UPLOAD)</c> calls for ring-sized requests. In steady state this
    /// stops moving while <see cref="UploadHeapReuses"/> keeps climbing — the ring doing its job.</summary>
    internal int UploadHeapCreates => _uploadHeapCreates;
    /// <summary>Cumulative <see cref="Stage"/> calls served by a heap popped from the ring (no create).</summary>
    internal int UploadHeapReuses => _uploadHeapReuses;

    public void Init(ID3D12Device* device, bool unifiedMemory = false)
    {
        _device = device;
        _uma = unifiedMemory;
        var experiment = FluentGpu.Hosting.EngineSwitches.ImageAtlas;
        _atlasGpuCopy = _uma && experiment is FluentGpu.Hosting.ImageAtlasExperiment.GpuCopy or FluentGpu.Hosting.ImageAtlasExperiment.GpuCopy256;
        if (_atlasGpuCopy && experiment == FluentGpu.Hosting.ImageAtlasExperiment.GpuCopy256) _pageSize = 2048;
        if (!_uma || _atlasGpuCopy)
        {
            _copyQueue = new UploadQueue(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY, "Image.CopyQueue");
            _copyQueue.Init(device);
        }
        if (_atlasGpuCopy)
            Diag.Line($"[d3d12] UMA image atlas EXPERIMENT {experiment}: copy-queue written simultaneous-access pages, page={_pageSize}px");
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

        // Query the texture allocation, not its upload footprint. Optional ROW_MAJOR descriptors may be rejected
        // here before CreateCommittedResource. In that case no pages are admitted; the packer's positive placeholder
        // is unreachable bookkeeping, never a measured allocation or a live census contribution.
        bool cpuWrittenPages = _uma && !_atlasGpuCopy;
        var pageDesc = DescribeTexture(_pageSize, _pageSize, cpuWrittenPages, simultaneous: !cpuWrittenPages);
        ulong pageBytes = AllocationBytes(&pageDesc);
        _atlasAllocationKnown = pageBytes != 0;
        _packer = new ImageAtlasPacker(_pageSize, _atlasAllocationKnown ? (long)pageBytes : 1,
            cpuWrittenPages ? ImageAtlasUpload.CpuWrite : ImageAtlasUpload.GpuCopy,
            maxPackedBucket: experiment == FluentGpu.Hosting.ImageAtlasExperiment.GpuCopy256 && _atlasGpuCopy ? 256 : ImageAtlasPacker.MaxPackedBucket);
    }

    /// <summary>The compute queue baked derivatives are written on (its fences gate their readiness).</summary>
    internal void AttachComputeQueue(UploadQueue queue) => _computeQueue = queue;

    private bool SideDone(byte queue, ulong fence) => queue switch
    {
        1 => _copyQueue is null || _copyQueue.IsComplete(fence),
        2 => _computeQueue is null || _computeQueue.IsComplete(fence),
        _ => true,
    };

    /// <summary>Published AND its side-queue batch (if any) completed: the frame may sample it.</summary>
    private bool Drawable(in Tex t) => t.Live && SideDone(t.FenceQueue, t.Fence);

    private int FindReplacing(int id)
    {
        for (int i = 0; i < _replacing.Count; i++) if (_replacing[i].Id == id) return i;
        return -1;
    }

    /// <summary>The placement the frame draws for <paramref name="id"/>: the current one when drawable, else the prior one
    /// it is replacing.</summary>
    private bool TryDrawable(int id, out Tex t)
    {
        if (_byId.TryGetValue(id, out t) && Drawable(in t)) return true;
        int r = FindReplacing(id);
        if (r >= 0) { t = _replacing[r].Prior; return true; }
        t = default;
        return false;
    }

    private static int BucketFor(int px) => px <= 64 ? 64 : px <= 128 ? 128 : px <= 256 ? 256 : px <= 512 ? 512 : px;

    /// <summary>True when a ≤128px thumbnail should be packed into a shared page rather than owning a texture. On UMA
    /// this also requires the ROW_MAJOR CPU-writable page probe to have succeeded (<see cref="_umaPagesDisabled"/>).</summary>
    private bool WantAtlas(int bucket)
        => _atlasAllocationKnown && bucket <= _packer.MaxBucket && _packer.CanPack(bucket) && !(_uma && _umaPagesDisabled);

    /// <summary>True when the frame can sample pixels for <paramref name="id"/> now (<see cref="TryGet"/> succeeds).</summary>
    public bool IsResident(int id) => TryDrawable(id, out _);

    private uint _contentSerial;

    /// <summary>The identity of the pixels a draw of <paramref name="id"/> samples NOW (0 = none drawable): it changes
    /// whenever different pixels become drawable under the same id — a replacement published in place, or a staged one
    /// whose side-queue fence passed (it supersedes the prior it kept drawable). A retained tile that drew the id under
    /// another serial holds stale pixels of it (a partial raster re-draws the image whole — D3D12Device.TileDamage.cs).</summary>
    public uint ContentSerial(int id) => TryDrawable(id, out var t) ? t.Serial : 0u;

    /// <summary>True when <paramref name="id"/> has pixels staged or on a side queue that have not landed yet — a frame that
    /// drew its placeholder instead is not the final picture (the tile holding it re-rasters once they land).</summary>
    public bool IsInFlight(int id)
        => _byId.TryGetValue(id, out var t) && (t.NeedsCopy || (t.Live && !SideDone(t.FenceQueue, t.Fence)));

    /// <summary>Set by <c>D3D12Device.DrainImageJobs</c> at the end of every drain: true while it holds an upload over its
    /// per-turn budget, so that job and everything queued behind it are still waiting in the upload queue (which cannot be
    /// peeked, so WHICH ids is unknown). The UI admitted them Ready already (+1-frame admission) and will not damage their
    /// tiles again, so every draw resolved meanwhile is provisional (<see cref="ResolveDraw"/>): a placeholder for an id
    /// not staged yet, or prior pixels a same-id replacement behind the hold will supersede. Render thread only.</summary>
    internal bool UploadBacklogHeld { get; set; }

    /// <summary>How a draw of <paramref name="id"/> resolves this frame: true when it samples pixels (<see cref="IsResident"/>
    /// — the current placement, or the prior one a replacement keeps published), false for the placeholder.
    /// <paramref name="provisional"/> is true when newer pixels for the id are staged or still on the copy / compute queue
    /// (<see cref="IsInFlight"/>) or may still wait behind a budget-held upload (<see cref="UploadBacklogHeld"/>), whichever
    /// of the two stands in for them: a retained tile that drew it is not the final picture and must re-raster once they land.</summary>
    public bool ResolveDraw(int id, out bool provisional)
    {
        provisional = UploadBacklogHeld || IsInFlight(id);
        return TryDrawable(id, out _);
    }

    /// <summary>The resolved SRV + the per-image sub-rect UV (origin+size in 0..1, half-texel inset) for the pipeline.</summary>
    public bool TryGet(int id, out D3D12_GPU_DESCRIPTOR_HANDLE srv, out RectF uv)
    {
        if (TryDrawable(id, out var t))
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
        if (TryGet(id, out _, out uv) && TryDrawable(id, out var t))
        {
            resource = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            return resource != null;
        }
        resource = null;
        return false;
    }

    /// <summary>Reserve a PRIVATE placement (pool / standalone, never a shared atlas page — the copy queue may be writing
    /// one of its cells) for a <paramref name="w"/>×<paramref name="h"/> baked derivative. The compute bake writes it at
    /// (0, 0) of <paramref name="destination"/> (COMMON, promoted on the compute queue and returned to COMMON there), then
    /// <see cref="CommitDerived"/> publishes it behind the batch's fence, or <see cref="AbandonDerived"/> returns it.</summary>
    internal bool TryReserveDerived(int w, int h, out ID3D12Resource* destination, out int token)
    {
        AssertRenderThread();
        destination = null; token = 0;
        int bucket = BucketFor(Math.Max(w, h));
        if (w <= 0 || h <= 0 || bucket > 512) return false;
        Pooled pt;
        if (_derivedPool.TryGetValue(bucket, out var stk) && stk.Count > 0) pt = stk.Pop();
        else
        {
            if (!TryAcquireSlot(out int slot)) return false;
            var res = CreateDerivedTexture(bucket);
            if (res == null) { _freeSlots.Push(slot); return false; }   // device-removed window: soft-fail
            CreateSrv(res, slot, out var srv, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM);
            pt = new Pooled { Resource = res, Srv = srv, Slot = slot, State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON };
        }
        Tex t = default;
        t.Resource = pt.Resource; t.Srv = pt.Srv; t.Slot = pt.Slot; t.Bucket = bucket; t.TexSize = bucket; t.State = pt.State;
        t.Derived = true; t.W = w; t.H = h;
        token = ++_nextReserveToken;
        _reserved.Add((token, t));
        destination = t.Resource;
        return true;
    }

    /// <summary>A compute batch (<paramref name="computeFence"/>) reads <paramref name="id"/>'s drawable pixels (a bake
    /// source): its placement is not reclaimed before that batch completes.</summary>
    internal void NoteComputeRead(int id, ulong computeFence)
    {
        if (_byId.TryGetValue(id, out var t) && Drawable(in t))
        {
            t.ComputeReadFence = computeFence;
            _byId[id] = t;
            return;
        }
        int r = FindReplacing(id);
        if (r >= 0)
        {
            var prior = _replacing[r].Prior;
            prior.ComputeReadFence = computeFence;
            _replacing[r] = (id, prior);
        }
    }

    /// <summary>Publish reserved derivative <paramref name="token"/> as <paramref name="id"/>'s pixels, drawable once compute
    /// batch <paramref name="computeFence"/> completed; the id's current pixels stay drawable until then.</summary>
    internal void CommitDerived(int token, int id, ulong computeFence)
    {
        AssertRenderThread();
        int r = FindReserved(token);
        if (r < 0) return;
        Tex t = _reserved[r].T;
        _reserved.RemoveAt(r);
        t.Live = true; t.NeedsCopy = false; t.Fence = computeFence; t.FenceQueue = 2;
        bool had = _byId.TryGetValue(id, out var prior);
        if (had) _pendingCopies.Remove(id);
        PublishReplacement(id, t, prior, reroute: had);
    }

    /// <summary>Return reserved derivative <paramref name="token"/> unpublished — behind compute batch
    /// <paramref name="computeFence"/> when one was recorded into it (0 = never written).</summary>
    internal void AbandonDerived(int token, ulong computeFence)
    {
        AssertRenderThread();
        int r = FindReserved(token);
        if (r < 0) return;
        Tex t = _reserved[r].T;
        _reserved.RemoveAt(r);
        t.Fence = computeFence; t.FenceQueue = computeFence != 0 ? (byte)2 : (byte)0;
        RetireCounted(ref t);
    }

    private int FindReserved(int token)
    {
        for (int i = 0; i < _reserved.Count; i++) if (_reserved[i].Token == token) return i;
        return -1;
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
        // Discrete: a placement already handed to the GPU (copied, or on the copy queue) is never rewritten either — the
        // copy queue runs beside the frames that may still sample it.
        bool published = had && (t.Live || t.Fence != 0);
        bool reroute = had && (_uma || published || t.Atlas != wantAtlas || t.Bucket != (wantAtlas ? bucket : (bucket <= 512 ? bucket : 0)));
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
                t.Srv = _pages[cell.Page].Srv; t.TexSize = _pageSize; t.Ox = cell.X; t.Oy = cell.Y;
                _atlasCount++;
            }
            else if (bucket <= 512)
            {
                if (!AcquirePooled(bucket, out var pt, allowPlaced: true)) return RejectCapacity();
                t.Atlas = false; t.Resource = pt.Resource; t.Srv = pt.Srv; t.Slot = pt.Slot; t.Bucket = bucket;
                t.Placed = pt.Placed;
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
                t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON; t.Live = false;   // COMMON for life, both paths
                CreateSrv(t.Resource, slot, out t.Srv);
            }
        }

        if (_uma && !(t.Atlas && _atlasGpuCopy))   // the gpucopy experiment's atlas cells take the staged copy-queue path below
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
                if (page.Mapped == null) return RejectReplacement(id, ref t, prior, reroute);
                t.W = w; t.H = h; t.RowPitch = rowBytes; t.Upload = null;
                WriteCell(page, new ImageAtlasCell(t.Page, t.Cell, t.Ox, t.Oy, t.Bucket, t.PageGen), pbgra8, w, h);
                page.Live = true;
                t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;   // page state is COMMON for life (I1)
                t.NeedsCopy = false; t.Live = true;
                PublishReplacement(id, t, prior, reroute);
                return ImageUploadResult.Accepted;
            }

            ID3D12Resource* umaTex = t.Resource;   // not packed ⇒ a private pool/standalone texture
            if (umaTex == null) return RejectReplacement(id, ref t, prior, reroute);
            t.W = w; t.H = h; t.RowPitch = rowBytes; t.Upload = null;
            D3D12_BOX box = new() { left = (uint)t.Ox, top = (uint)t.Oy, front = 0, right = (uint)(t.Ox + w), bottom = (uint)(t.Oy + h), back = 1 };
            int hr;
            // Opaque UNKNOWN layout supports Map with a null ppData. Each placed resource was activated and fenced
            // before leasing, and is independently byte-disjoint from neighboring resources on its heap.
            if (t.Placed.IsValid)
            {
                D3D12_RANGE noRead = default;
                hr = (int)umaTex->Map(0, &noRead, null);
                if (hr < 0)
                {
                    _smallImages!.WriteFailed(t.Placed, "Map", hr);
                    NoteResourceFault("Image.Placed.Map");
                    return RetryCommittedOrReject(id, ref t, prior, reroute, pbgra8, w, h);
                }
            }
            if (t.Placed.IsValid && _refuseNextPlacedWriteForProbe)
            {
                _refuseNextPlacedWriteForProbe = false;
                hr = unchecked((int)0x80070057); // E_INVALIDARG: deterministic capability refusal, no device removal
            }
            else
                fixed (byte* src = pbgra8)
                    hr = (int)umaTex->WriteToSubresource(0, &box, src, (uint)rowBytes, (uint)((long)rowBytes * h));
            if (t.Placed.IsValid) umaTex->Unmap(0, null);
            if (hr < 0)
            {
                // Device-removed window: WriteToSubresource fails like the CreateCommittedResource/Map calls do on the
                // discrete path. Soft-fail identically (publish + retire this id's placement, reject) — never throw past
                // the render-thread seam.
                NoteResourceFault("Image.WriteToSubresource");
                if (t.Placed.IsValid)
                {
                    _smallImages!.WriteFailed(t.Placed, "WriteToSubresource", hr);
                    return RetryCommittedOrReject(id, ref t, prior, reroute, pbgra8, w, h);
                }
                return RejectReplacement(id, ref t, prior, reroute);
            }
            t.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;   // sampled via implicit promotion; never barriered
            t.NeedsCopy = false; t.Live = true;
            PublishReplacement(id, t, prior, reroute);
            return ImageUploadResult.Accepted;   // NOT queued into _pendingCopies — FlushUploads has no copy/barrier to do
        }

        // Take a staging heap from the ring (or keep this id's own when it still fits). A NON-null Upload here was never
        // recorded into a command list — FlushCopies nulls it the moment it records the copy — so the outgrown heap goes
        // straight back to its bucket with no fence gate (nothing on the GPU can be reading it).
        if (t.Upload == null || t.UploadCapacity < uploadBytes)
        {
            if (t.Upload != null) { RecycleUpload(t.Upload, t.UploadCapacity); t.Upload = null; t.UploadCapacity = 0; }
            t.Upload = AcquireUpload(uploadBytes, out t.UploadCapacity);
        }
        t.W = w; t.H = h; t.RowPitch = rowPitch;

        // Device-removed window: retire the failed fresh placement but preserve an existing published replacement.
        if (t.Upload == null) return RejectReplacement(id, ref t, prior, reroute);

        void* p = null;
        // Map is the second device touch that fails on a removed device; an unchecked HRESULT left `p` null and the
        // row copy below wrote through it (an access violation, not even a catchable managed throw).
        if ((int)t.Upload->Map(0, null, &p) < 0 || p == null)
        {
            NoteResourceFault("Image.Upload.Map");
            return RejectReplacement(id, ref t, prior, reroute);
        }
        byte* dst = (byte*)p;
        fixed (byte* src = pbgra8)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * rowPitch, rowPitch, rowBytes);
        t.Upload->Unmap(0, null);

        t.NeedsCopy = true;
        PublishReplacement(id, t, prior, reroute);
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

    /// <summary>Retire only the failed placement; a rerouted id keeps its prior pixels and descriptor published.</summary>
    private ImageUploadResult RejectReplacement(int id, ref Tex t, Tex prior, bool reroute)
    {
        _pendingCopies.Remove(id);
        RetireCounted(ref t);
        if (reroute) _byId[id] = prior;   // old pixels/SRV remain published until a replacement succeeds
        else _byId.Remove(id);
        return RejectDeviceFault();
    }

    private ImageUploadResult RetryCommittedOrReject(int id, ref Tex t, Tex prior, bool reroute,
        ReadOnlySpan<byte> pixels, int w, int h)
    {
        if ((int)_device->GetDeviceRemovedReason() < 0) return RejectReplacement(id, ref t, prior, reroute);
        // The old id was never unpublished. Retire only the failed, unpublished placed lease, then retry once through
        // the now-disabled bucket's committed path. A capability refusal must not trigger ImageCache's terminal reject.
        RetirePlacement(ref t);
        _poolCount--;
        return Stage(id, pixels, w, h);
    }

    private void PublishReplacement(int id, Tex t, Tex prior, bool reroute)
    {
        t.Serial = ++_contentSerial;   // new pixels under the same id (an LQIP → full-res swap, a re-bake): a new identity
        if (reroute)
        {
            // Keep the prior pixels drawable while the new ones are still on a side queue (the first such prior only:
            // a replacement superseded before it landed is simply retired).
            if (!Drawable(in t) && Drawable(in prior) && FindReplacing(id) < 0) _replacing.Add((id, prior));
            else RetireCounted(ref prior);
        }
        _byId[id] = t;
    }

    /// <summary>Retire a placement and drop it from the atlas / pool census.</summary>
    private void RetireCounted(ref Tex t)
    {
        bool atlas = t.Atlas;
        int bucket = t.Derived ? 0 : t.Bucket;
        RetirePlacement(ref t);
        if (atlas) _atlasCount--; else if (bucket > 0) _poolCount--;
    }

    /// <summary>Retire every prior placement whose replacement is now drawable (or whose id went away).</summary>
    private void SweepReplacing()
    {
        for (int i = _replacing.Count - 1; i >= 0; i--)
        {
            var (id, prior) = _replacing[i];
            if (_byId.TryGetValue(id, out var t) && !Drawable(in t)) continue;
            _replacing.RemoveAt(i);
            RetireCounted(ref prior);
        }
    }

    /// <summary>Evict an image (residency dropped it): return its atlas cell / pool texture for reuse and release its
    /// upload buffer — all DEFERRED behind the frame fence (an in-flight frame may still sample it).</summary>
    public void Free(int id)
    {
        AssertRenderThread();   // seam Step 1: render-confined under async
        if (_byId.Remove(id, out var t))
        {
            _pendingCopies.Remove(id);
            RetireCounted(ref t);
        }
        int r = FindReplacing(id);
        if (r >= 0)
        {
            var prior = _replacing[r].Prior;
            _replacing.RemoveAt(r);
            RetireCounted(ref prior);
        }
    }

    // Queue this Tex's GPU resources for deferred reclaim (cell return / pool return / standalone release + upload).
    private void RetirePlacement(ref Tex t)
    {
        var r = new Retire
        {
            Fence = _retireFence, Upload = t.Upload, UploadCapacity = t.UploadCapacity, State = t.State,
            SideFence = t.Fence, SideQueue = t.FenceQueue,   // a side queue may still be writing it
            ComputeReadFence = t.ComputeReadFence,           // ...or a bake still reading it
        };
        if (t.Placed.IsValid) { r.Kind = 4; r.Placed = t.Placed; r.Slot = t.Slot; }
        else if (t.Derived) { r.Kind = 6; r.Bucket = t.Bucket; r.Resource = t.Resource; r.Srv = t.Srv; r.Slot = t.Slot; }
        else if (t.Atlas) { r.Kind = 2; r.Cell = new ImageAtlasCell(t.Page, t.Cell, t.Ox, t.Oy, t.Bucket, t.PageGen); }
        else if (t.Bucket > 0) { r.Kind = 1; r.Bucket = t.Bucket; r.Resource = t.Resource; r.Srv = t.Srv; r.Slot = t.Slot; }
        else { r.Kind = 0; r.Resource = t.Resource; r.Slot = t.Slot; }
        _retired.Add(r);
        t.Resource = null; t.Upload = null; t.UploadCapacity = 0;
    }

    /// <summary>Frame top: reclaim only resources whose last possible GPU-use fence completed, then record the
    /// deferred copies (atlas cells into their page; pool/standalone into the whole texture) and transition to PSR.</summary>
    /// <summary>Image texture copies recorded by the most recent <see cref="FlushUploads"/>, and their pixel bytes
    /// (width × height × 4). Always-on plain counters (render thread) — the device folds them into its per-submit
    /// <c>GpuFrameCounters</c>.</summary>
    internal int LastFlushCopies { get; private set; }
    /// <inheritdoc cref="LastFlushCopies"/>
    internal long LastFlushBytes { get; private set; }

    public void FlushUploads(ID3D12GraphicsCommandList* cmd, ulong submitFence, ulong completedFence)
    {
        AssertRenderThread();   // seam Step 1: render-confined under async (always render-side; assert makes it explicit)
        LastFlushCopies = 0;
        LastFlushBytes = 0;
        _retireFence = submitFence;
        ReclaimCompleted(completedFence);
        _smallImages?.RecordActivations(cmd, submitFence);

        FlushCopies();
    }

    /// <summary>Fence-only maintenance, safe without opening a command list or requesting another presentation.</summary>
    internal void ReclaimCompleted(ulong completedFence)
    {
        AssertRenderThread();
        if (completedFence == ulong.MaxValue) return; // removal sentinel is not a completed GPU submission
        SweepReplacing();
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence > completedFence || !SideDone(_retired[i].SideQueue, _retired[i].SideFence)
                || !SideDone(2, _retired[i].ComputeReadFence)) continue;
            var r = _retired[i];
            // The staging heap rides on ANY kind (a Free before the copy flushed carries it too): ring-sized heaps go
            // back to their bucket, one-offs and over-cap returns are released. Kinds 3 and 5 exist only for this.
            if (r.Upload != null) RecycleUpload(r.Upload, r.UploadCapacity);
            switch (r.Kind)
            {
                case 0: if (r.Resource != null) { D3D12MemoryDiagnostics.Release(r.Resource, "Image.Texture"); r.Resource->Release(); } QuarantineSlot(r.Slot); break;
                case 1: ReleasePooled(r.Bucket, new Pooled { Resource = r.Resource, Srv = r.Srv, Slot = r.Slot, State = r.State }); break;
                case 2: ReleaseAtlasCell(r.Cell); break;
                case 3: break;   // one-off upload heap: released above
                case 6: ReleaseDerived(r.Bucket, new Pooled { Resource = r.Resource, Srv = r.Srv, Slot = r.Slot, State = r.State }); break;
                case 5: break;   // ring upload heap: returned to its bucket above
                case 4:
                    if (!_smallImages!.Release(r.Placed, r.Fence, completedFence))
                        throw new InvalidOperationException("Invalid or stale placed-image return.");
                    QuarantineSlot(r.Slot);
                    break;
            }
            _retired.RemoveAt(i);
        }
        _smallImages?.Reclaim(completedFence, keepWarm: !_noPooling);
        AdvanceSlotQuarantine();
    }

    // CANDIDATE FIX (INCIDENT 2026-09, defensive) — see _slotQuarantine's own doc. A slot that just cleared its
    // OWNING resource's fence is not pushed straight to _freeSlots; it sits here for SlotQuarantinePolicy.InitialGenerations more
    // ReclaimCompleted calls first. Never used for a slot that was reserved but never handed to any GPU work (e.g.
    // AcquireCell's own failed-create path a few lines below) — those are still safe to reuse immediately and push
    // to _freeSlots directly, unchanged.
    private void QuarantineSlot(int slot) => _slotQuarantine.Add((slot, FluentGpu.Rhi.SlotQuarantinePolicy.InitialGenerations));

    // One "generation" = one ReclaimCompleted call (a real submitted frame's FlushUploads, or a skip-turn's
    // ReclaimCompletedUploads — both are forward progress on the render thread). Walk backwards so RemoveAt is cheap
    // and doesn't disturb the not-yet-visited prefix, same shape as the _retired sweep above.
    private void AdvanceSlotQuarantine()
    {
        for (int i = _slotQuarantine.Count - 1; i >= 0; i--)
        {
            var (slot, left) = _slotQuarantine[i];
            var (next, ready) = FluentGpu.Rhi.SlotQuarantinePolicy.Advance(left);
            if (ready) { _freeSlots.Push(slot); _slotQuarantine.RemoveAt(i); }
            else _slotQuarantine[i] = (slot, next);
        }
    }

    private void FlushCopies()
    {
        if (_pendingCopies.Count == 0) return;
        ID3D12GraphicsCommandList* copy = null;
        ulong batch = 0;
        for (int i = 0; i < _pendingCopies.Count; i++)
        {
            int id = _pendingCopies[i];
            if (!_byId.TryGetValue(id, out var t) || !t.NeedsCopy) continue;

            ID3D12Resource* destTex = t.Atlas ? _pages[t.Page].Tex : t.Resource;
            if (destTex == null) continue;
            if (copy == null) { copy = _copyQueue!.Open(); batch = _copyQueue.PendingValue; }

            // COMMON → (copy-queue promotion) COPY_DEST → (decay at the batch end) COMMON: no barrier, on either queue.
            D3D12_TEXTURE_COPY_LOCATION dst = default;
            dst.pResource = destTex; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION srcLoc = default;
            srcLoc.pResource = t.Upload; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Width = (uint)t.W;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Height = (uint)t.H;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Depth = 1;
            srcLoc.Anonymous.PlacedFootprint.Footprint.RowPitch = (uint)t.RowPitch;
            FluentGpu.Interop.Generated.ID3D12GraphicsCommandListVtbl.CopyTextureRegion(copy, &dst, (uint)t.Ox, (uint)t.Oy, 0, &srcLoc, null);
            LastFlushCopies++;
            LastFlushBytes += (long)t.W * t.H * 4;

            t.NeedsCopy = false; t.Live = true;
            t.Fence = batch; t.FenceQueue = 1;
            if (t.Atlas) _pages[t.Page].Live = true;
            if (t.Upload != null)
            {
                // The staging heap is busy until the copy batch's fence passes, then it returns to the ring (Kind 5)
                // or, for a one-off above the largest bucket, is released (Kind 3).
                _retired.Add(new Retire
                {
                    Kind = t.UploadCapacity <= MaxUploadBucket ? 5 : 3,
                    Upload = t.Upload,
                    UploadCapacity = t.UploadCapacity,
                    SideFence = batch, SideQueue = 1,
                });
                t.Upload = null; t.UploadCapacity = 0;
            }
            _byId[id] = t;
        }
        if (copy != null) _copyQueue!.Submit();
        _pendingCopies.Clear();
    }

    // ── pool ──────────────────────────────────────────────────────────────────
    private bool AcquirePooled(int bucket, out Pooled pt, bool allowPlaced = false)
    {
        // GPU-produced derivatives deliberately do not opt in: these resources are CPU-written COMMON for life.
        if (allowPlaced && _uma && (_umaPagesDisabled || !_atlasAllocationKnown) && bucket <= 128)
        {
            _smallImages ??= new SmallImageHeapPool(_device);
            if (_smallImages.TryAcquire(bucket, out var lease))
            {
                if (!TryAcquireSlot(out int placedSlot))
                {
                    _smallImages.Release(lease, 0, 0); // unpublished lease has no GPU use
                    pt = default; return false;
                }
                CreateSrv(lease.Resource, placedSlot, out var placedSrv);
                pt = new Pooled { Resource = lease.Resource, Srv = placedSrv, Slot = placedSlot,
                    State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, Placed = lease };
                return true;
            }
        }
        if (_pool.TryGetValue(bucket, out var stk) && stk.Count > 0) { pt = stk.Pop(); System.Threading.Interlocked.Decrement(ref _pooledFreeMirror); return true; }
        if (!TryAcquireSlot(out int slot)) { pt = default; return false; }
        var res = CreateTexture(bucket, bucket);            // cold pool growth (the only CreateTexture in steady state)
        if (res == null) { _freeSlots.Push(slot); pt = default; return false; }   // device-removed: give the slot back, reject
        CreateSrv(res, slot, out var srv);
        pt = new Pooled
        {
            Resource = res, Srv = srv, Slot = slot,
            State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON   // COMMON for life, both paths (never barriered)
        };
        return true;
    }

    // Hidden-window Shallow stage: while set, nothing returns to a free pool, the staging ring or a warm placed-heap page - every
    // freed texture / heap is released instead. Without it the evictions the host enqueues for the unpinned images land AFTER the
    // one-shot ReleaseIdle (UI enqueue, render drain, fence-gated reclaim) and refill up to MaxFreePooledTexturesPerBucket per bucket.
    private bool _noPooling;
    internal bool NoPooling => _noPooling;

    /// <summary>Hidden-window stage on/off (the owning thread: the render thread, or the UI thread in SingleThread mode).
    /// Turning it on does not release what is already pooled - <see cref="ReleaseIdle"/> does.</summary>
    internal void SetNoPooling(bool on)
    {
        AssertRenderThread();
        _noPooling = on;
    }

    /// <summary>Hidden-window Shallow stage: release every FREE pooled texture (full and derived buckets), every free staging-ring
    /// heap and every warm placed-heap page. Pooled textures go through the fence-deferred standalone retire (Kind 0), so any frame
    /// still sampling one finishes first; ring heaps are free by construction (nothing records into a heap in the ring). Resident
    /// images, atlas pages with live cells and occupied placed pages are untouched. Returns the textures retired.</summary>
    internal int ReleaseIdle(ulong completedFence)
    {
        AssertRenderThread();
        int n = 0;
        foreach (var stk in _pool.Values)
            while (stk.Count > 0)
            {
                var pt = stk.Pop();
                System.Threading.Interlocked.Decrement(ref _pooledFreeMirror);
                _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
                n++;
            }
        foreach (var stk in _derivedPool.Values)
            while (stk.Count > 0)
            {
                var pt = stk.Pop();
                _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
                n++;
            }
        for (int i = 0; i < _uploadRing.Length; i++)
        {
            var ring = _uploadRing[i];
            if (ring is null) continue;
            while (ring.Count > 0)
            {
                var heap = (ID3D12Resource*)ring.Pop();
                D3D12MemoryDiagnostics.Release(heap, "Image.Upload");
                heap->Release();
            }
            _uploadRing[i] = null;
        }
        _uploadRingBytes = 0;
        _smallImages?.Reclaim(completedFence, keepWarm: false);
        return n;
    }

    private void ReleaseDerived(int bucket, Pooled pt)
    {
        if (_noPooling)
        {
            _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
            return;
        }
        if (!_derivedPool.TryGetValue(bucket, out var stk)) { stk = new Stack<Pooled>(); _derivedPool[bucket] = stk; }
        if (stk.Count >= MaxFreeDerivedPerBucket)
        {
            _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
            return;
        }
        stk.Push(pt);
    }

    /// <summary>A bucket² RGBA8 texture the compute bake writes through an unordered-access view (RGBA8 is the typed-UAV
    /// store every D3D12 device supports; BGRA8's is optional). COMMON for life: the compute queue barriers it to
    /// UNORDERED_ACCESS for its write and back, the frames promote it to read.</summary>
    private ID3D12Resource* CreateDerivedTexture(int bucket)
    {
        D3D12_RESOURCE_DESC td = DescribeTexture(bucket, bucket, rowMajor: false);
        td.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        td.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        ID3D12Resource* tex = null;
        if ((int)_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
        { NoteResourceFault("Image.CreateDerived"); return null; }
        TrackTexture(tex, $"Image.Derived.{bucket}x{bucket} RGBA8", &td);
        return tex;
    }

    private void ReleasePooled(int bucket, Pooled pt)
    {
        if (_noPooling)
        {
            _retired.Add(new Retire { Fence = _retireFence, Kind = 0, Resource = pt.Resource, Slot = pt.Slot });
            return;
        }
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
                // Always-on, once per session, through Diag.Line — NOT Console.Error, which never reaches the host log
                // sink and is lost entirely in a packaged NativeAOT run with no console, and NOT Diag.Count, whose
                // recording methods are [Conditional("DEBUG"),Conditional("FLUENTGPU_DIAG")] and vanish in exactly the
                // Release build where this answer matters. The stage and the HRESULT ride along: they are what says
                // whether the layout or only the persistent mapping was refused.
                Diag.Line($"[d3d12] UMA atlas pages unavailable stage={_umaPageFaultStage ?? "create"} hr=0x{_umaPageFaultHr:X8}" +
                          " (ROW_MAJOR CPU-writable TEXTURE2D) — thumbnails fall back to private textures");
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
        // promotion, never a copy destination). A discrete page is COMMON for life too: the copy queue writes its cells
        // (simultaneous access — the frames keep sampling the other cells meanwhile) and nothing ever barriers it.
        pg.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
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
        if (pg.Slot >= 0) { QuarantineSlot(pg.Slot); pg.Slot = -1; }
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

    /// <summary>The BGRA8 TEXTURE2D desc. <paramref name="rowMajor"/> is for CPU-WRITTEN atlas pages only: <c>Map</c> on
    /// a texture is legal only for a ROW_MAJOR layout, and the linear layout is what makes a per-cell write byte-disjoint
    /// from every other cell on the page (invariant I3). Every other texture keeps UNKNOWN so the driver picks its
    /// optimal (tiled/compressed) layout — including the private UMA textures, which are populated by
    /// <c>WriteToSubresource</c> exactly as they ship today.</summary>
    private static D3D12_RESOURCE_DESC DescribeTexture(int w, int h, bool rowMajor, bool simultaneous = false)
    {
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = (ulong)w; td.Height = (uint)h; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Layout = rowMajor
            ? D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR
            : D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        if (simultaneous) td.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
        return td;
    }

    /// <summary>Device-reported allocation requirement, not physical residency or upload-buffer size. Zero means
    /// unknown/unsupported, never an inferred pixel or copy-footprint estimate. Thin wrapper over the shared
    /// <see cref="D3D12MemoryDiagnostics.AllocationBytes"/> query (every RT/DSV/texture owner shares it) that keeps
    /// this store's atlas-admission-specific logging.</summary>
    private ulong AllocationBytes(D3D12_RESOURCE_DESC* td)
    {
        ulong bytes = D3D12MemoryDiagnostics.AllocationBytes(_device, td);
        if (bytes == 0)
            Diag.Line($"[d3d12] image allocation requirement unknown width={td->Width} height={td->Height} layout={td->Layout}; excluded from byte totals, atlas admission disabled for unknown page sizes");
        return bytes;
    }

    // What the driver says one square bucket texture commits (64 / 128 / 256 / 512), written the first time each is created and read
    // by the host's budget code from another thread: a plain long array, one writer (the render thread), aligned 64-bit reads.
    private readonly long[] _measuredBucketBytes = new long[4];

    private void NoteMeasuredBucket(int w, int h, ulong bytes)
    {
        if (bytes == 0 || w != h || w < 64 || w > 512 || (w & (w - 1)) != 0) return;   // only the pool's square power-of-two buckets
        int i = w <= 64 ? 0 : w <= 128 ? 1 : w <= 256 ? 2 : 3;
        if (System.Threading.Volatile.Read(ref _measuredBucketBytes[i]) == 0) System.Threading.Volatile.Write(ref _measuredBucketBytes[i], (long)bytes);
    }

    /// <summary>The driver-measured commit of one square image texture of <paramref name="bucket"/> (0 = not created yet). Any thread.</summary>
    public long MeasuredCommittedBytes(int bucket)
    {
        if (bucket < 64 || bucket > 512) return 0;
        int i = bucket <= 64 ? 0 : bucket <= 128 ? 1 : bucket <= 256 ? 2 : 3;
        return System.Threading.Volatile.Read(ref _measuredBucketBytes[i]);
    }

    private ulong TrackTexture(ID3D12Resource* texture, string name, D3D12_RESOURCE_DESC* descriptor)
    {
        ulong bytes = AllocationBytes(descriptor);
        D3D12MemoryDiagnostics.Track(texture, D3D12MemoryDiagnostics.NameOrUnknown(name, bytes), bytes);
        return bytes;
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
            // The size joins the CLASS KEY (a dot, not a space): NameKey cuts at the first space, so the old name
            // collapsed every bucket into one `Image.Texture.Uma` row and the census could not say whether 88 MB was
            // 600 thumbnails or 90 heroes. Per-bucket rows are what make the commit-vs-decode over-charge legible.
            NoteMeasuredBucket(w, h, TrackTexture(tex, $"Image.Texture.Uma.{w}x{h} BGRA8", &td));
            return tex;
        }

        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        // NULL on failure, never a throw: on a removed device (DXGI_ERROR_DEVICE_REMOVED, 0x887A0005) every create here
        // fails, and this runs on the fgpu-render thread INSIDE the image drain — a throw past that seam is unobserved
        // and kills the process. Every caller treats null as "could not admit" (media-pipeline.md §4.1).
        // COMMON for life: the copy (or compute) queue promotes it for its write, the frames promote it to read.
        if ((int)_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
        { NoteResourceFault("Image.CreateTexture"); return null; }
        NoteMeasuredBucket(w, h, TrackTexture(tex, $"Image.Texture.{w}x{h} BGRA8", &td));   // per-bucket class key, see the UMA arm
        return tex;
    }

    /// <summary>Create ONE atlas page resource for the index the packer reserved.
    /// <para><b>Discrete:</b> a DEFAULT-heap UNKNOWN-layout SIMULTANEOUS-ACCESS texture in COMMON — the copy queue writes
    /// a new cell while the frames sample the others, and nothing ever barriers it.</para>
    /// <para><b>UMA:</b> a ROW_MAJOR texture on a CUSTOM(L0/WRITE_BACK) heap created in COMMON and MAPPED once for the
    /// resource's life. It is only ever GPU-READ, so it promotes COMMON→PIXEL_SHADER_RESOURCE implicitly on first sample
    /// and is NEVER barriered (I1). The page is zero-filled once here so the gutter texels are transparent black rather
    /// than whatever the heap held — deterministic, and it makes any hypothetical bleed visibly nothing instead of
    /// garbage. ROW_MAJOR TEXTURE2D creation is driver-optional: a failure disables UMA page packing for the session and
    /// every thumbnail falls back to the proven private-texture path, so this is a capability probe, not a switch.</para></summary>
    private ID3D12Resource* CreatePageTexture(bool cpuWritten, out byte* mapped, out int rowPitch, out ulong bytes)
    {
        mapped = null; rowPitch = 0; bytes = 0;
        D3D12_RESOURCE_DESC td = DescribeTexture(_pageSize, _pageSize, rowMajor: cpuWritten, simultaneous: !cpuWritten);
        ID3D12Resource* tex = null;

        if (!cpuWritten)
        {
            D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
            if ((int)_device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex) < 0)
            { NoteResourceFault("Image.CreateAtlasPage"); return null; }
            bytes = TrackTexture(tex, $"Image.AtlasPage {_pageSize}x{_pageSize} BGRA8", &td);
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
        // NOT NoteResourceFault: this is a CAPABILITY PROBE, and a ROW_MAJOR CPU-writable TEXTURE2D is driver-optional
        // with no cap bit to ask first. Counting a refused probe as a resource fault made it indistinguishable from
        // device removal at DrainImageJobs, whose fault DELTA calls NoteIfDeviceLost. The HRESULT and the stage are
        // recorded instead, and AcquireCell logs them once.
        int hr = (int)_device->CreateCommittedResource(&up, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&tex);
        if (hr < 0) { _umaPageFaultHr = hr; _umaPageFaultStage = "create"; return null; }

        void* p = null;
        hr = (int)tex->Map(0, null, &p);
        if (hr < 0 || p == null)
        {
            // Distinct from a create refusal, and the distinction decides the fix: a texture that EXISTS but cannot be
            // persistently mapped can still be written with WriteToSubresource on the same ROW_MAJOR layout.
            _umaPageFaultHr = p == null && hr >= 0 ? 0 : hr;
            _umaPageFaultStage = "map";
            tex->Release();
            return null;
        }

        rowPitch = (int)fp.Footprint.RowPitch;
        if (rowPitch <= 0) rowPitch = _pageSize * 4;
        mapped = (byte*)p;
        new Span<byte>(mapped, (int)Math.Min((ulong)int.MaxValue, (ulong)rowPitch * (ulong)_pageSize)).Clear();
        bytes = TrackTexture(tex, $"Image.AtlasPage.Uma {_pageSize}x{_pageSize} BGRA8", &td);
        return tex;
    }

    private void CreateSrv(ID3D12Resource* tex, int slot, out D3D12_GPU_DESCRIPTOR_HANDLE gpu,
        DXGI_FORMAT format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = format;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
        sd.Anonymous.Texture2D.MipLevels = 1;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = _srvCpu0; cpu.ptr += (nuint)((ulong)slot * _srvInc);
        _device->CreateShaderResourceView(tex, &sd, cpu);
        gpu = _srvGpu0; gpu.ptr += (ulong)slot * _srvInc;
    }

    // ── upload heap ring ──────────────────────────────────────────────────────
    /// <summary>The ring bucket (a power of two in [<see cref="MinUploadBucket"/>, <see cref="MaxUploadBucket"/>]) that
    /// holds <paramref name="bytes"/>, or 0 when the request is above the largest bucket (one-off heap).</summary>
    private static int UploadBucketFor(long bytes)
    {
        if (bytes > MaxUploadBucket) return 0;
        int b = MinUploadBucket;
        while (b < bytes) b <<= 1;
        return b;
    }

    private static int UploadBucketIndex(int bucket)
        => System.Numerics.BitOperations.Log2((uint)bucket) - System.Numerics.BitOperations.Log2((uint)MinUploadBucket);

    /// <summary>A staging heap able to hold <paramref name="bytes"/>: popped from the ring bucket when one is free,
    /// created cold otherwise (a one-off of the exact size above the largest bucket). <paramref name="capacity"/> is
    /// what the heap actually holds — the bucket size, or the exact one-off size — and travels with the heap through
    /// <see cref="Tex.UploadCapacity"/> / <see cref="Retire.UploadCapacity"/> so <see cref="RecycleUpload"/> can route it.
    /// Null on a device fault, exactly like <see cref="CreateUpload"/>.</summary>
    private ID3D12Resource* AcquireUpload(long bytes, out long capacity)
    {
        int bucket = UploadBucketFor(bytes);
        if (bucket == 0)
        {
            capacity = bytes;
            return CreateUpload((uint)bytes, "Image.Upload");
        }
        capacity = bucket;
        var stk = _uploadRing[UploadBucketIndex(bucket)];
        if (stk is { Count: > 0 })
        {
            var reused = (ID3D12Resource*)stk.Pop();
            _uploadRingBytes -= bucket;
            _uploadHeapReuses++;
            return reused;
        }
        // Cold growth. The bucket joins the class KEY (NameKey cuts at the first space) so the census shows the ring
        // per bucket instead of one undifferentiated `Image.Upload` row.
        var created = CreateUpload((uint)bucket, $"Image.UploadRing.{bucket >> 10}K");
        if (created != null) _uploadHeapCreates++;
        return created;
    }

    /// <summary>Hand a staging heap back once nothing can read it (fence passed, or never recorded). A ring-sized heap
    /// returns to its bucket while the FREE ring stays under <see cref="MaxPooledUploadBytes"/>; otherwise — one-off
    /// size, or the cap would be exceeded — the heap is released. Capacity is exact by construction (every ring heap
    /// was created at its bucket size), so the range test alone tells a ring heap from a one-off.</summary>
    private void RecycleUpload(ID3D12Resource* upload, long capacity)
    {
        if (!_noPooling && capacity >= MinUploadBucket && capacity <= MaxUploadBucket && _uploadRingBytes + capacity <= MaxPooledUploadBytes)
        {
            int idx = UploadBucketIndex((int)capacity);
            (_uploadRing[idx] ??= new Stack<nint>(4)).Push((nint)upload);
            _uploadRingBytes += capacity;
            return;
        }
        D3D12MemoryDiagnostics.Release(upload, "Image.Upload");
        upload->Release();
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

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    public void Dispose()
    {
        _copyQueue?.Dispose(); _copyQueue = null;   // waits for every copy batch first
        foreach (var (_, prior) in _replacing)
            if (prior.Resource != null && !prior.Placed.IsValid) { D3D12MemoryDiagnostics.Release(prior.Resource, "Image.Texture"); prior.Resource->Release(); }
        _replacing.Clear();
        foreach (var (_, t) in _reserved)
            if (t.Resource != null && !t.Placed.IsValid) { D3D12MemoryDiagnostics.Release(t.Resource, "Image.Texture"); t.Resource->Release(); }
        _reserved.Clear();
        foreach (var t in _byId.Values)
        {
            if (t.Upload != null) { D3D12MemoryDiagnostics.Release(t.Upload, "Image.Upload"); t.Upload->Release(); }
            if (t.Resource != null && !t.Placed.IsValid) { D3D12MemoryDiagnostics.Release(t.Resource, "Image.Texture"); t.Resource->Release(); }
        }
        foreach (var r in _retired)
        {
            if (r.Upload != null) { D3D12MemoryDiagnostics.Release(r.Upload, "Image.Upload"); r.Upload->Release(); }
            if (r.Kind != 2 && r.Resource != null) { D3D12MemoryDiagnostics.Release(r.Resource, "Image.Texture"); r.Resource->Release(); }
        }
        foreach (var stk in _pool.Values)
            foreach (var p in stk)
                if (p.Resource != null) { D3D12MemoryDiagnostics.Release(p.Resource, "Image.Texture"); p.Resource->Release(); }
        foreach (var stk in _derivedPool.Values)
            foreach (var p in stk)
                if (p.Resource != null) { D3D12MemoryDiagnostics.Release(p.Resource, "Image.Texture"); p.Resource->Release(); }
        _derivedPool.Clear();
        for (int i = 0; i < _uploadRing.Length; i++)
        {
            var ring = _uploadRing[i];
            if (ring is null) continue;
            while (ring.Count > 0)
            {
                var heap = (ID3D12Resource*)ring.Pop();
                D3D12MemoryDiagnostics.Release(heap, "Image.Upload");
                heap->Release();
            }
            _uploadRing[i] = null;
        }
        _uploadRingBytes = 0;
        foreach (var pg in _pages)
            if (pg.Tex != null)
            {
                if (pg.Mapped != null) { pg.Tex->Unmap(0, null); pg.Mapped = null; }
                D3D12MemoryDiagnostics.Release(pg.Tex, "Image.AtlasPage");
                pg.Tex->Release();
                pg.Tex = null;
            }
        _byId.Clear(); _retired.Clear(); _pool.Clear(); _pages.Clear();
        _smallImages?.Dispose(); _smallImages = null;
        _packer?.Clear();
        System.Threading.Volatile.Write(ref _atlasPageMirror, 0);
        System.Threading.Volatile.Write(ref _atlasPageBytesMirror, 0);
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Image.SrvHeap"); _srvHeap->Release(); _srvHeap = null; }
    }
}
