using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The pooled heaps behind the retained-tile textures (<see cref="FluentGpu.Render.Tiles.TileMemory.PlacedTiles"/>): DEFAULT
/// heaps of <see cref="HeapBytes"/> (render targets only), cut into <see cref="BlockBytes"/> blocks tracked by a bitmap. A tile
/// texture is a placed resource on a run of blocks; freeing it returns the run to the bitmap and frees no memory, so the driver's
/// per-allocation release (~0.3 ms each on the owner's UMA driver, whatever the size: <c>--fg heap-probe</c>) never runs while
/// the app runs. Only a whole EMPTY heap is ever released (<see cref="ReleaseOneEmpty"/>), by the idle
/// path once the app has composited nothing for a while. Placement: the lowest-index heap with a run that fits, best fit inside
/// it, so live tiles pack toward the first heaps and the last ones drain. A surface larger than half a heap (a page-sized group
/// or blur scratch) gets a LARGE heap of its own, rounded up to 4 MiB; freed, that heap stays as a spare any later large surface
/// of at least half its size reuses (best fit), and is released at idle like the others. Render-thread-owned.
/// </summary>
internal sealed unsafe class TileHeapPool
{
    public const ulong BlockBytes = 64 * 1024;
    /// <summary>A full 1024x512 BGRA8 tile occupies 33 blocks on the owner's driver (2 MiB + 64 KiB): a heap holds exactly eight
    /// (16.5 MiB). Small enough that the free runs of the one partly filled heap a compacted rest keeps stay small; a heap's
    /// create / release costs ~0.1-0.4 ms (the --fg heap-probe), so more heaps cost nothing that matters.</summary>
    public const int BlocksPerHeap = 8 * 33;
    public const ulong HeapBytes = BlockBytes * BlocksPerHeap;
    private const int Words = (BlocksPerHeap + 63) / 64;

    private readonly ID3D12Device* _device;
    private ID3D12Heap*[] _heaps = new ID3D12Heap*[8];
    private int[] _used = new int[8];          // blocks in use per heap
    private ulong[] _bits = new ulong[8 * Words];   // 1 = block in use
    private int _slots;                         // heap slots ever used (a released heap leaves a null slot, reused first)
    private bool _notZeroedRefused;

    // LARGE heaps: one surface each (offset 0), kept as spares once free. Heap index space: LargeBase + j.
    private const int LargeBase = 1 << 16;
    private const int LargeRoundBlocks = 64;          // 4 MiB
    private const int MaxLargeBlocks = 1024;          // 64 MiB: anything larger stays a committed texture
    private struct Large { public ID3D12Heap* Ptr; public int Blocks; public bool Used; }
    private Large[] _large = new Large[4];
    private int _largeSlots;

    public TileHeapPool(ID3D12Device* device) { _device = device; }

    /// <summary>Heaps created / released (each a driver memory allocation / release).</summary>
    public long HeapCreates { get; private set; }
    public long HeapReleases { get; private set; }
    /// <summary>Heaps created with <c>D3D12_HEAP_FLAG_CREATE_NOT_ZEROED</c> (the driver accepted the flag).</summary>
    public bool NotZeroed => !_notZeroedRefused;

    public int HeapCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null) n++;
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null) n++;
            return n;
        }
    }
    public int EmptyCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null && _used[i] == 0) n++;
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null && !_large[j].Used) n++;
            return n;
        }
    }
    /// <summary>Free blocks inside small heaps that still hold a surface (bytes): what only a compaction can give back.</summary>
    public long FragmentBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null && _used[i] > 0) b += (long)(BlocksPerHeap - _used[i]) * (long)BlockBytes;
            return b;
        }
    }

    /// <summary>Bytes of heaps nothing is placed on (small heaps with no block in use, spare large heaps).</summary>
    public long EmptyBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null && _used[i] == 0) b += (long)HeapBytes;
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null && !_large[j].Used) b += (long)_large[j].Blocks * (long)BlockBytes;
            return b;
        }
    }

    public long HeldBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null) b += (long)HeapBytes;
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null) b += (long)_large[j].Blocks * (long)BlockBytes;
            return b;
        }
    }
    public long UsedBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < _slots; i++) if (_heaps[i] != null) b += (long)_used[i] * (long)BlockBytes;
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null && _large[j].Used) b += (long)_large[j].Blocks * (long)BlockBytes;
            return b;
        }
    }

    public ID3D12Heap* Heap(int heap) => heap >= LargeBase ? _large[heap - LargeBase].Ptr : _heaps[heap];

    /// <summary>Blocks a resource of <paramref name="bytes"/> occupies; 0 = too large for any heap (a committed texture).</summary>
    public static int BlocksFor(ulong bytes)
    {
        ulong n = (bytes + BlockBytes - 1) / BlockBytes;
        return n == 0 || n > MaxLargeBlocks ? 0 : (int)n;
    }

    /// <summary>A run of <paramref name="blocks"/> free blocks: the lowest-index heap with a run that fits (best fit inside it),
    /// else a new heap. False only when a new heap cannot be created.</summary>
    public bool Allocate(int blocks, out int heap, out int block) => Allocate(blocks, -1, true, out heap, out block);

    /// <summary><see cref="Allocate(int, out int, out int)"/> that never uses heap <paramref name="exclude"/> and creates a new
    /// heap only when <paramref name="allowNew"/> (compaction moves tiles only into memory that already exists).</summary>
    public bool Allocate(int blocks, int exclude, bool allowNew, out int heap, out int block)
    {
        if (blocks > BlocksPerHeap / 2) { block = 0; return AllocateLarge(blocks, allowNew, out heap); }
        for (int h = 0; h < _slots; h++)
        {
            if (h == exclude || _heaps[h] == null || BlocksPerHeap - _used[h] < blocks) continue;
            int b = BestFit(h, blocks);
            if (b < 0) continue;
            Mark(h, b, blocks, true);
            heap = h; block = b;
            return true;
        }
        if (!allowNew) { heap = block = -1; return false; }
        int slot = -1;
        for (int h = 0; h < _slots; h++) if (_heaps[h] == null) { slot = h; break; }
        if (slot < 0)
        {
            if (_slots == _heaps.Length)
            {
                var grown = new ID3D12Heap*[_slots * 2];
                for (int i = 0; i < _slots; i++) grown[i] = _heaps[i];
                _heaps = grown;
                Array.Resize(ref _used, _slots * 2);
                Array.Resize(ref _bits, _slots * 2 * Words);
            }
            slot = _slots++;
        }
        ID3D12Heap* p = CreateHeap(HeapBytes);
        if (p == null)
        {
            while (_slots > 0 && _heaps[_slots - 1] == null) _slots--;
            heap = block = -1;
            return false;
        }
        _heaps[slot] = p;
        _used[slot] = 0;
        Array.Clear(_bits, slot * Words, Words);
        Mark(slot, 0, blocks, true);
        heap = slot; block = 0;
        return true;
    }

    // A free LARGE heap of at least the request and at most twice it (best fit), else a new one rounded up to 4 MiB.
    private bool AllocateLarge(int blocks, bool allowNew, out int heap)
    {
        int best = -1;
        for (int j = 0; j < _largeSlots; j++)
        {
            ref Large l = ref _large[j];
            if (l.Ptr == null || l.Used || l.Blocks < blocks || l.Blocks > 2 * blocks) continue;
            if (best < 0 || l.Blocks < _large[best].Blocks) best = j;
        }
        if (best < 0)
        {
            if (!allowNew) { heap = -1; return false; }
            int size = (blocks + LargeRoundBlocks - 1) / LargeRoundBlocks * LargeRoundBlocks;
            ID3D12Heap* p = CreateHeap((ulong)size * BlockBytes);
            if (p == null) { heap = -1; return false; }
            for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr == null) { best = j; break; }
            if (best < 0)
            {
                if (_largeSlots == _large.Length) Array.Resize(ref _large, _largeSlots * 2);
                best = _largeSlots++;
            }
            _large[best] = new Large { Ptr = p, Blocks = size };
        }
        _large[best].Used = true;
        Version++;
        heap = LargeBase + best;
        return true;
    }

    /// <summary>Return a run to its heap (the resource placed on it is released; its last use the GPU has passed).</summary>
    public void Free(int heap, int block, int blocks)
    {
        if (heap >= LargeBase)
        {
            int j = heap - LargeBase;
            if ((uint)j < (uint)_largeSlots && _large[j].Ptr != null) { _large[j].Used = false; Version++; }
            return;
        }
        if ((uint)heap >= (uint)_slots || _heaps[heap] == null) return;
        Mark(heap, block, blocks, false);
    }

    /// <summary>Bumped by every allocation, free and heap release: a compaction plan that failed is not retried until it moves.</summary>
    public int Version { get; private set; }

    /// <summary>The non-empty heap holding the fewest blocks (the highest index on a tie), when at least two heaps exist: the one
    /// whose tiles an idle compaction tries to move into the others' free runs. −1 = none.</summary>
    public int Sparsest()
    {
        int best = -1, n = 0;
        for (int h = 0; h < _slots; h++)
        {
            if (_heaps[h] == null) continue;
            n++;
            if (_used[h] == 0) continue;
            if (best < 0 || _used[h] <= _used[best]) best = h;
        }
        return n >= 2 ? best : -1;
    }

    /// <summary>Free blocks in every heap but <paramref name="exclude"/>.</summary>
    public int FreeBlocksExcept(int exclude)
    {
        int f = 0;
        for (int h = 0; h < _slots; h++) if (h != exclude && _heaps[h] != null) f += BlocksPerHeap - _used[h];
        return f;
    }

    public int UsedBlocks(int heap) => (uint)heap < (uint)_slots && _heaps[heap] != null ? _used[heap] : 0;

    /// <summary>Release one EMPTY heap — a spare large heap first, else the highest-index small one (one memory release).
    /// False when no heap is empty.</summary>
    public bool ReleaseOneEmpty()
    {
        for (int j = _largeSlots - 1; j >= 0; j--)
        {
            if (_large[j].Ptr == null || _large[j].Used) continue;
            ReleaseLarge(j);
            return true;
        }
        for (int h = _slots - 1; h >= 0; h--)
        {
            if (_heaps[h] == null || _used[h] != 0) continue;
            ReleaseHeap(h);
            return true;
        }
        return false;
    }

    /// <summary>Release every heap (device recovery / teardown: every placed resource is already released).</summary>
    public void ReleaseAll()
    {
        for (int h = 0; h < _slots; h++) if (_heaps[h] != null) ReleaseHeap(h);
        _slots = 0;
        for (int j = 0; j < _largeSlots; j++) if (_large[j].Ptr != null) ReleaseLarge(j);
        _largeSlots = 0;
    }

    private void Log(string what, ulong bytes)
    {
        int small = 0, smallUsed = 0, large = 0, largeUsed = 0;
        for (int i = 0; i < _slots; i++) if (_heaps[i] != null) { small++; smallUsed += _used[i]; }
        System.Text.StringBuilder sizes = new();
        for (int j = 0; j < _largeSlots; j++)
            if (_large[j].Ptr != null) { large++; if (_large[j].Used) largeUsed++; sizes.Append(_large[j].Blocks / 16).Append(_large[j].Used ? "u " : "f "); }
        Console.Error.WriteLine($"[tile-heaps] {what} {bytes / 1048576.0:F1}MiB small={small} usedBlocks={smallUsed}/{small * BlocksPerHeap} " +
            $"large={large} used={largeUsed} [{sizes}] held={HeldBytes / 1048576.0:F1}MiB used={UsedBytes / 1048576.0:F1}MiB t={Environment.TickCount64}");
    }

    private void ReleaseLarge(int j)
    {
        ulong bytes = (ulong)_large[j].Blocks * BlockBytes;
        D3D12MemoryDiagnostics.Release(_large[j].Ptr, "Tiles.Heap");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _large[j].Ptr->Release();
        if (FluentGpu.Render.Tiles.TileMemory.HeapLog) Log($"release-large {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3}ms", bytes);
        _large[j] = default;
        HeapReleases++;
        Version++;
        while (_largeSlots > 0 && _large[_largeSlots - 1].Ptr == null) _largeSlots--;
    }

    private void ReleaseHeap(int h)
    {
        D3D12MemoryDiagnostics.Release(_heaps[h], "Tiles.Heap");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _heaps[h]->Release();
        if (FluentGpu.Render.Tiles.TileMemory.HeapLog) Log($"release {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3}ms", HeapBytes);
        _heaps[h] = null;
        _used[h] = 0;
        HeapReleases++;
        Version++;
        while (_slots > 0 && _heaps[_slots - 1] == null) _slots--;
    }

    private ID3D12Heap* CreateHeap(ulong bytes)
    {
        D3D12_HEAP_DESC hd = default;
        hd.SizeInBytes = bytes;
        hd.Alignment = 65536;
        hd.Properties.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        hd.Flags = D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_RT_DS_TEXTURES;
        ID3D12Heap* p = null;
        // Every placed tile is discarded and cleared before its first raster reads anything, so a heap's initial contents are
        // never observed: ask the driver not to zero them (refused on runtimes without the flag: retry without it, once).
        if (!_notZeroedRefused)
        {
            D3D12_HEAP_DESC nz = hd;
            nz.Flags |= D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_CREATE_NOT_ZEROED;
            if (_device->CreateHeap(&nz, __uuidof<ID3D12Heap>(), (void**)&p) < 0 || p == null) { _notZeroedRefused = true; p = null; }
        }
        if (p == null && _device->CreateHeap(&hd, __uuidof<ID3D12Heap>(), (void**)&p) < 0) p = null;
        if (p == null) return null;
        D3D12MemoryDiagnostics.Track(p, "Tiles.Heap", bytes);
        HeapCreates++;
        if (FluentGpu.Render.Tiles.TileMemory.HeapLog) Log("create", bytes);
        return p;
    }

    // The smallest run of free blocks in heap h that holds n (-1 = none).
    private int BestFit(int h, int n)
    {
        int best = -1, bestLen = int.MaxValue, run = 0, start = 0;
        int w0 = h * Words;
        for (int b = 0; b <= BlocksPerHeap; b++)
        {
            bool used = b == BlocksPerHeap || (_bits[w0 + (b >> 6)] & (1UL << (b & 63))) != 0;
            if (!used) { if (run == 0) start = b; run++; continue; }
            if (run >= n && run < bestLen) { best = start; bestLen = run; if (run == n) break; }
            run = 0;
            // skip whole used words
            while (b + 1 < BlocksPerHeap && ((b + 1) & 63) == 0 && _bits[w0 + ((b + 1) >> 6)] == ulong.MaxValue) b += 64;
        }
        return best;
    }

    private void Mark(int h, int b, int n, bool used)
    {
        int w0 = h * Words;
        for (int i = b; i < b + n; i++)
        {
            ulong m = 1UL << (i & 63);
            if (used) _bits[w0 + (i >> 6)] |= m; else _bits[w0 + (i >> 6)] &= ~m;
        }
        _used[h] += used ? n : -n;
        Version++;
    }
}
