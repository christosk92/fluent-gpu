using System;

namespace FluentGpu.Rhi;

/// <summary>How an image-atlas page's texels are written — the ONE decision that fixes whether the page ever needs a
/// <c>COPY_DEST</c> resource transition, and therefore whether it can ride the UMA (Adreno) path at all.</summary>
public enum ImageAtlasUpload : byte
{
    /// <summary>Discrete/non-UMA (and the <c>--fg img-atlas=gpucopy</c> UMA experiment): decoded pixels land in an <c>UPLOAD</c>
    /// staging buffer and reach the page through a <c>CopyTextureRegion</c> on a COPY queue. The page is a simultaneous-access
    /// texture, <c>COMMON</c> for life: the copy queue promotes it to <c>COPY_DEST</c> for its write and it decays back, so no
    /// explicit <c>ResourceBarrier</c> is ever recorded for it (the per-flush <c>COPY_DEST → PIXEL_SHADER_RESOURCE</c> pair was
    /// the pre-retained-tiles path). It has never run on the Adreno.</summary>
    GpuCopy = 0,
    /// <summary>UMA: the page is a CPU-writable <c>ROW_MAJOR</c> texture on a <c>CUSTOM</c>(L0/WRITE_BACK) heap, written
    /// with a plain row-by-row memcpy through a persistent map and sampled straight out of <c>COMMON</c> by implicit
    /// promotion. It is GPU-<i>read</i>-only for its whole life: no staging buffer, no <c>CopyTextureRegion</c>, and NO
    /// <c>COPY_DEST</c> transition — the exact barrier the Qualcomm Adreno UMD mishandles (adreno-hang-fixes.md M1).</summary>
    CpuWrite = 1,
}

/// <summary>One packed cell: the page it lives on, its slot index in that page's grid, its top-left texel origin, the
/// bucket (cell side in px) and the page's generation at acquire time. <c>default</c> is the "no cell" value.</summary>
public readonly struct ImageAtlasCell
{
    public ImageAtlasCell(int page, int index, int x, int y, int bucket, int generation)
    {
        Page = page; Index = index; X = x; Y = y; Bucket = bucket; Generation = generation;
    }

    public int Page { get; }
    public int Index { get; }
    public int X { get; }
    public int Y { get; }
    public int Bucket { get; }
    /// <summary>The owning page's generation when this cell was acquired. A page's generation bumps every time its
    /// backing resource is (re)created, so a placement recorded against a released page is detectable rather than a
    /// silent read of a recycled texture.</summary>
    public int Generation { get; }

    public bool IsValid => Bucket > 0;
}

/// <summary>
/// The portable (TerraFX-free, engine-side) CPU bookkeeping for the small-image atlas that
/// <c>FluentGpu.Rhi.D3D12.ImageTextureStore</c> drives: the per-bucket cell grid, page growth/retirement, the LIFO
/// free list per page, the page generations, and the O(1) census (bytes and count are <b>per page</b> — a packed cell is
/// not a resource and is never counted as one). It owns NO GPU object; the store creates/releases the page resource for
/// the index this class hands out, which is what makes the whole packing policy unit-testable headlessly.
/// </summary>
/// <remarks>
/// <para><b>Why packing matters on UMA.</b> Every <c>CreateCommittedResource</c> texture is rounded up to
/// <c>D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT</c> (64 KiB) and placed in the driver's resident write-combine
/// segments. A 64² BGRA8 thumbnail is 16 KiB of pixels in a 64 KiB commit — a 4× byte overhead <i>and</i> one resident
/// resource per thumbnail. ~1000 cover thumbnails therefore cost ~62 MiB and ~1000 resources for ~16 MiB of pixels.
/// Packed into shared pages the same 1000 thumbnails are a handful of resources and a handful of SRV slots.</para>
///
/// <para><b>Bleed (the sampling contract).</b> Cells are laid out on a <see cref="Gutter"/>-separated grid: cell
/// <c>i</c> starts at <c>Gutter + i*(bucket+Gutter)</c>, so with the default gutter of 1 every cell is ringed by a
/// one-texel moat. That is belt-and-suspenders: the store's UV already hands the sampler
/// <c>[origin+0.5 … origin+size-0.5]</c> in texels, and with <c>MIN_MAG_MIP_LINEAR</c> + <c>ADDRESS_CLAMP</c> +
/// <c>MipLevels=1</c> a bilinear footprint spans at most ±0.5 texel around the sample point, so it provably stays
/// inside <c>[origin … origin+size]</c> — the cell — at any magnification. The gutter is what keeps that true if mips
/// or anisotropic filtering are ever added to the image pipeline. Cost: 15×15 instead of 16×16 cells per 1024² page at
/// bucket 64 (225 vs 256), 7×7 instead of 8×8 at bucket 128 (49 vs 64).</para>
///
/// <para><b>Fence lifetime (the invariant the Adreno fix rests on).</b> A cell's texels are written EXACTLY ONCE, while
/// the cell is unpublished — either on a page whose resource was just created (never submitted, never sampled) or on a
/// cell that came back through the store's fence-deferred return, i.e. only after the GPU fenced past every frame that
/// could still have recorded it. A re-stage of an already-resident id acquires a FRESH cell and retires the old one, so
/// a published cell is immutable. The CPU therefore never writes texels a submitted frame may be sampling. Cell-level
/// (rather than page-level) scope is sound only because a <see cref="ImageAtlasUpload.CpuWrite"/> page is
/// <c>ROW_MAJOR</c>: the layout is linear and uncompressed, so texel-disjoint is byte-disjoint — there is no driver
/// swizzle and no UBWC metadata that a write to one cell could share with another.</para>
///
/// <para>Zero allocation on the hot path: acquire/release touch preallocated <c>int[]</c> free stacks. The only
/// allocations are the page-array and free-stack growth on cold page creation.</para>
/// </remarks>
public sealed class ImageAtlasPacker
{
    /// <summary>Largest decode bucket (px) that packs into a shared page. Above it an image gets its own texture: a
    /// 256² BGRA8 commit is already 256 KiB, so the 64 KiB rounding overhead is under 0.1% and sharing a page would
    /// only trade that away for coarser eviction.</summary>
    public const int MaxPackedBucket = 128;

    private struct PageSlot
    {
        public int Bucket;        // 0 ⇒ this slot has no page (never created, or retired)
        public int Used;
        public int Generation;
        public int[]? Free;       // LIFO stack of free cell indices (allocated on the slot's first Prepare)
        public int FreeCount;
        public bool HasResource;  // the store created a backing resource for this slot
    }

    private PageSlot[] _slots;
    private int _slotCount;
    private int _livePages;
    private int _cellsInUse;
    private int _peakLivePages;

    /// <param name="pageSide">Page side in texels (square).</param>
    /// <param name="pageBytes">The page resource's device-reported allocation requirement — the census unit.
    /// The store passes <c>GetResourceAllocationInfo().SizeInBytes</c>, not a copy footprint or pixel estimate.
    /// If unavailable, the store must disable admission; a positive placeholder may initialize an unused packer.</param>
    /// <param name="upload">Fixes the barrier posture for every page this packer hands out.</param>
    /// <param name="gutter">Texels of separation between cells and between a cell and the page edge (see the remarks).</param>
    /// <param name="maxPackedBucket">Largest bucket (px) that packs; <see cref="MaxPackedBucket"/> unless an experiment widens it
    /// (<c>--fg img-atlas=gpucopy256</c>: 256 on 2048 px pages, 7x7 cells).</param>
    public ImageAtlasPacker(int pageSide, long pageBytes, ImageAtlasUpload upload, int gutter = 1, int maxPackedBucket = MaxPackedBucket)
    {
        if (pageSide <= 0) throw new ArgumentOutOfRangeException(nameof(pageSide));
        if (pageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(pageBytes));
        if (gutter < 0) throw new ArgumentOutOfRangeException(nameof(gutter));
        if (maxPackedBucket <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackedBucket));
        MaxBucket = maxPackedBucket;
        PageSide = pageSide;
        PageBytes = pageBytes;
        Upload = upload;
        Gutter = gutter;
        _slots = new PageSlot[4];
    }

    /// <summary>Largest bucket this packer packs (<see cref="MaxPackedBucket"/> by default).</summary>
    public int MaxBucket { get; }
    public int PageSide { get; }
    /// <summary>Committed bytes of ONE page — the census unit. Cells are not resources and have no byte line of their own.</summary>
    public long PageBytes { get; }
    public ImageAtlasUpload Upload { get; }
    public int Gutter { get; }

    /// <summary>True when a page from this packer is ever a <c>CopyTextureRegion</c> destination, i.e. when the store
    /// must emit the <c>… → COPY_DEST → PIXEL_SHADER_RESOURCE</c> pair for it. False for
    /// <see cref="ImageAtlasUpload.CpuWrite"/> pages — the barrier-free invariant, stated as a value the store and the
    /// gates can both read instead of a comment nobody can assert on.</summary>
    public bool RequiresCopyDestTransition => Upload == ImageAtlasUpload.GpuCopy;

    // ── census (all O(1)) ─────────────────────────────────────────────────────
    /// <summary>Pages with a live backing resource.</summary>
    public int LivePageCount => _livePages;
    /// <summary>Highest <see cref="LivePageCount"/> this packer ever reached.</summary>
    public int PeakLivePageCount => _peakLivePages;
    /// <summary>Cells currently holding an image.</summary>
    public int CellsInUse => _cellsInUse;
    /// <summary>Resident GPU bytes attributable to the atlas — <c>LivePageCount × PageBytes</c>. O(pages), never O(images).</summary>
    public long TotalPageBytes => _livePages * PageBytes;
    /// <summary>Page slots ever created (live + retired-but-reusable). Not a resource count — see <see cref="LivePageCount"/>.</summary>
    public int SlotCount => _slotCount;

    /// <summary>True when <paramref name="bucket"/> is a packable thumbnail bucket that fits at least one cell.</summary>
    public bool CanPack(int bucket) => bucket > 0 && bucket <= MaxBucket && CellsPerAxis(bucket) > 0;

    /// <summary>Cells per axis for <paramref name="bucket"/> — the grid the gutter leaves room for.</summary>
    public int CellsPerAxis(int bucket)
    {
        if (bucket <= 0) return 0;
        int pitch = bucket + Gutter;
        int n = (PageSide - Gutter) / pitch;
        return n < 0 ? 0 : n;
    }

    /// <summary>Cells one page holds at <paramref name="bucket"/>.</summary>
    public int CellCapacity(int bucket) { int n = CellsPerAxis(bucket); return n * n; }

    /// <summary>Texel origin of grid slot <paramref name="index"/> at <paramref name="bucket"/>.</summary>
    public bool TryCellOrigin(int bucket, int index, out int x, out int y)
    {
        int n = CellsPerAxis(bucket);
        if (n <= 0 || (uint)index >= (uint)(n * n)) { x = y = 0; return false; }
        int pitch = bucket + Gutter;
        x = Gutter + (index % n) * pitch;
        y = Gutter + (index / n) * pitch;
        return true;
    }

    /// <summary>Fraction of a full page's committed bytes that is live image pixels — the honest packing efficiency
    /// (a 1024² page at bucket 64 with a 1-texel gutter holds 225 × 16 KiB = 3.52 MiB of a 4 MiB page).</summary>
    public double FullPageEfficiency(int bucket)
        => PageBytes <= 0 ? 0.0 : (double)CellCapacity(bucket) * bucket * bucket * 4 / PageBytes;

    // ── allocation ────────────────────────────────────────────────────────────
    /// <summary>Take a free cell from an EXISTING page of this bucket. False ⇒ the caller must
    /// <see cref="TryReservePage"/> + <see cref="CommitPage"/> a new page first.</summary>
    public bool TryAcquire(int bucket, out ImageAtlasCell cell)
    {
        if (CanPack(bucket))
        {
            for (int i = 0; i < _slotCount; i++)
            {
                ref var s = ref _slots[i];
                if (!s.HasResource || s.Bucket != bucket || s.FreeCount == 0) continue;
                int[]? free = s.Free;
                if (free == null) continue;
                int index = free[--s.FreeCount];
                s.Used++;
                _cellsInUse++;
                TryCellOrigin(bucket, index, out int x, out int y);
                cell = new ImageAtlasCell(i, index, x, y, bucket, s.Generation);
                return true;
            }
        }
        cell = default;
        return false;
    }

    /// <summary>Reserve a page SLOT for <paramref name="bucket"/> (reusing a retired slot when one is free). The store
    /// then creates the resource and calls <see cref="CommitPage"/> on success or <see cref="AbandonPage"/> on failure —
    /// so a device-removed create never leaves a phantom page in the census.</summary>
    public int TryReservePage(int bucket)
    {
        if (!CanPack(bucket)) return -1;
        for (int i = 0; i < _slotCount; i++)
            if (!_slots[i].HasResource && _slots[i].Bucket == 0)
                return Prepare(i, bucket);
        if (_slotCount == _slots.Length) Array.Resize(ref _slots, _slots.Length * 2);
        int slot = _slotCount++;
        _slots[slot] = default;
        return Prepare(slot, bucket);
    }

    private int Prepare(int slot, int bucket)
    {
        ref var s = ref _slots[slot];
        int capacity = CellCapacity(bucket);
        int[] free = s.Free is { } existing && existing.Length >= capacity ? existing : new int[capacity];
        s.Free = free;
        s.Bucket = bucket;
        s.Used = 0;
        s.HasResource = false;
        // Push in DESCENDING order so the LIFO pops 0,1,2,… — a fresh page fills its grid front-to-back, which keeps a
        // partially-filled page's live cells contiguous (nicer for the driver's resident pages and for reading a dump).
        s.FreeCount = 0;
        for (int i = capacity - 1; i >= 0; i--) free[s.FreeCount++] = i;
        return slot;
    }

    /// <summary>The store created the page resource: mark the slot live and take its first cell.</summary>
    public ImageAtlasCell CommitPage(int slot)
    {
        if ((uint)slot >= (uint)_slotCount) return default;
        ref var s = ref _slots[slot];
        if (s.HasResource || s.Bucket == 0 || s.FreeCount == 0) return default;
        int[]? free = s.Free;
        if (free == null) return default;
        s.HasResource = true;
        s.Generation++;
        _livePages++;
        if (_livePages > _peakLivePages) _peakLivePages = _livePages;
        int index = free[--s.FreeCount];
        s.Used = 1;
        _cellsInUse++;
        TryCellOrigin(s.Bucket, index, out int x, out int y);
        return new ImageAtlasCell(slot, index, x, y, s.Bucket, s.Generation);
    }

    /// <summary>The store could not create the page resource (device removed / SRV heap exhausted): release the slot for
    /// reuse without ever counting it as live.</summary>
    public void AbandonPage(int slot)
    {
        if ((uint)slot >= (uint)_slotCount) return;
        ref var s = ref _slots[slot];
        if (s.HasResource) return;
        s.Bucket = 0; s.Used = 0; s.FreeCount = 0;
    }

    /// <summary>Return an evicted cell. The caller MUST already have fence-gated this (the cell's texels may still be
    /// read by an in-flight frame until then). <paramref name="pageBecameEmpty"/> is true when this was the page's last
    /// live cell, i.e. the store should release the page resource and call <see cref="RetirePage"/>.</summary>
    public void Release(in ImageAtlasCell cell, out bool pageBecameEmpty)
    {
        pageBecameEmpty = false;
        if (!cell.IsValid || (uint)cell.Page >= (uint)_slotCount) return;
        ref var s = ref _slots[cell.Page];
        // A stale placement (its page was retired and the slot recycled) must never poison the current page's free list.
        if (!s.HasResource || s.Generation != cell.Generation || s.Bucket != cell.Bucket) return;
        int[]? free = s.Free;
        if (free == null || s.Used == 0 || s.FreeCount >= free.Length) return;
        free[s.FreeCount++] = cell.Index;
        s.Used--;
        if (_cellsInUse > 0) _cellsInUse--;
        pageBecameEmpty = s.Used == 0;
    }

    /// <summary>The store released an empty page's resource: drop it out of the census and free the slot for reuse at
    /// any bucket. The generation stays bumped so a placement from the old page can never match the next one.</summary>
    public void RetirePage(int slot)
    {
        if ((uint)slot >= (uint)_slotCount) return;
        ref var s = ref _slots[slot];
        if (!s.HasResource) return;
        s.HasResource = false;
        s.Bucket = 0;
        s.FreeCount = 0;
        s.Used = 0;
        if (_livePages > 0) _livePages--;
    }

    /// <summary>Live cells on <paramref name="slot"/> (0 when the slot holds no page).</summary>
    public int PageUsed(int slot) => (uint)slot < (uint)_slotCount ? _slots[slot].Used : 0;
    /// <summary>The bucket <paramref name="slot"/>'s page is grided for (0 when the slot holds no page).</summary>
    public int PageBucket(int slot) => (uint)slot < (uint)_slotCount ? _slots[slot].Bucket : 0;
    public bool PageHasResource(int slot) => (uint)slot < (uint)_slotCount && _slots[slot].HasResource;
    public int PageGeneration(int slot) => (uint)slot < (uint)_slotCount ? _slots[slot].Generation : 0;
    /// <summary>Free cells left on <paramref name="slot"/>.</summary>
    public int PageFreeCells(int slot) => (uint)slot < (uint)_slotCount ? _slots[slot].FreeCount : 0;

    /// <summary>Drop every page (device-lost rebuild). The store has already released the resources.</summary>
    public void Clear()
    {
        for (int i = 0; i < _slotCount; i++)
        {
            ref var s = ref _slots[i];
            s.HasResource = false; s.Bucket = 0; s.Used = 0; s.FreeCount = 0;
        }
        _livePages = 0;
        _cellsInUse = 0;
    }
}
