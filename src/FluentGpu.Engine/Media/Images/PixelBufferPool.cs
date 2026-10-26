using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// Bounded pool of decoded-pixel buffers (<see cref="PixelBlock"/>, native memory; media-pipeline.md §3 "recycled CPU
/// pixel blocks"). Thread-safe (decode workers Rent, UI/render threads Return). The cap bounds RETAINED (idle) bytes, not
/// in-flight bytes: Rent always succeeds (allocates a fresh block when nothing parked fits), Return parks a block only
/// while RetainedBytes stays under the cap and otherwise frees it at once.
/// <para>Blocks are sized to the request (a 4 KiB page multiple), not to a power of two: the old power-of-two ladder gave a
/// 1024x576 cover (2.25 MiB) a 4 MiB array. A parked block serves a later request it fits within a quarter of
/// (<see cref="FitSlack"/>), so a page of same-sized covers reuses one block per in-flight decode.</para>
/// </summary>
public sealed class PixelBufferPool
{
    /// <summary>Requests are rounded up to this (a page): nothing finer pays.</summary>
    public const int Granule = 4 * 1024;
    /// <summary>A parked block serves a request when its capacity is at most need + need / FitSlack (25 % over).</summary>
    public const int FitSlack = 4;

    public const long DefaultRetainedCapBytes = 32L * 1024 * 1024;

    // Parked blocks, ascending by capacity (best fit = the first one that is large enough). Small: a page's worth.
    private readonly List<PixelBlock> _parked = new();
    private readonly long _retainedCapBytes;
    private long _retainedBytes;       // bytes PARKED (never in-flight bytes)
    private long _peakRetainedBytes;
    private long _liveBytes;           // every block alive: parked + rented
    private long _peakLiveBytes;

    public PixelBufferPool(long retainedCapBytes = DefaultRetainedCapBytes)
    {
        _retainedCapBytes = Math.Max(0, retainedCapBytes);
    }

    public long RetainedCapBytes => _retainedCapBytes;
    public long RetainedBytes => Interlocked.Read(ref _retainedBytes);
    public long PeakRetainedBytes => Interlocked.Read(ref _peakRetainedBytes);
    /// <summary>Native bytes of every block alive, parked or rented (decodes in flight, queued uploads).</summary>
    public long LiveBytes => Interlocked.Read(ref _liveBytes);
    public long PeakLiveBytes => Interlocked.Read(ref _peakLiveBytes);

    // Raised AFTER parking, outside the lock, on the returning thread. Hosts coalesce this into one cold deadline and
    // unsubscribe on pool replacement/disposal. No upload, signal write, or frame is requested here.
    internal event Action<PixelBufferPool>? BufferRetained;

    /// <summary>Rent a block of at least <paramref name="minBytes"/>. ALWAYS succeeds: the smallest parked block that fits
    /// (within <see cref="FitSlack"/>) is reused, otherwise a fresh one is allocated. Caller returns exactly once.</summary>
    public PixelBlock Rent(int minBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minBytes);
        int need = (int)Math.Min(int.MaxValue, ((long)Math.Max(minBytes, 1) + Granule - 1) / Granule * Granule);
        long fitLimit = need + (long)need / FitSlack;
        lock (_parked)
        {
            int i = FirstAtLeast(need);
            if (i < _parked.Count && _parked[i].Capacity <= fitLimit)
            {
                PixelBlock block = _parked[i];
                _parked.RemoveAt(i);
                Interlocked.Add(ref _retainedBytes, -block.Capacity);
                return block;
            }
        }
        var fresh = new PixelBlock(need);
        UpdatePeak(ref _peakLiveBytes, Interlocked.Add(ref _liveBytes, need));
        return fresh;
    }

    /// <summary>Return a rented block. Parks it while the retained total stays under the cap; otherwise frees it (its
    /// memory goes back at once). Never blocks on budget.</summary>
    public void Return(PixelBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int len = block.Capacity;
        long cur = Interlocked.Read(ref _retainedBytes);      // reserve BEFORE parking so the cap is never exceeded
        while (true)
        {
            long after = cur + len;
            if (after > _retainedCapBytes) { Release(block); return; }
            long prev = Interlocked.CompareExchange(ref _retainedBytes, after, cur);
            if (prev == cur) { UpdatePeak(ref _peakRetainedBytes, after); break; }
            cur = prev;
        }
        lock (_parked)
        {
            AssertNotDoubleReturned(block);
            _parked.Insert(FirstAtLeast(len), block);
        }
        BufferRetained?.Invoke(this);
    }

    /// <summary>Cold one-shot maintenance: free every parked block, never in-flight ones.</summary>
    public void Trim()
    {
        lock (_parked)
        {
            foreach (var block in _parked)
            {
                Interlocked.Add(ref _retainedBytes, -block.Capacity);
                Release(block);
            }
            _parked.Clear();
            _parked.TrimExcess();
        }
    }

    private void Release(PixelBlock block)
    {
        Interlocked.Add(ref _liveBytes, -block.Capacity);
        block.Free();
    }

    // Lower bound by capacity in the ascending parked list (caller holds the lock).
    private int FirstAtLeast(int capacity)
    {
        int lo = 0, hi = _parked.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_parked[mid].Capacity < capacity) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static void UpdatePeak(ref long peak, long candidate)
    {
        long seen = Interlocked.Read(ref peak);
        while (candidate > seen)
        {
            long prev = Interlocked.CompareExchange(ref peak, candidate, seen);
            if (prev == seen) return;
            seen = prev;
        }
    }

    // Erased in Release like the D3D12Device confinement tripwires: a double-Return hands one block to two renters.
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertNotDoubleReturned(PixelBlock block)
    {
        foreach (var parked in _parked)
            if (ReferenceEquals(parked, block))
                throw new InvalidOperationException("PixelBufferPool: block returned twice");
    }
}
