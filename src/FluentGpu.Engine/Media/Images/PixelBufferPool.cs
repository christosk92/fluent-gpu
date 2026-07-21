using System.Numerics;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// Bounded CPU pixel-buffer pool (media-pipeline.md §3 "recycled CPU pixel blocks, bucket-sized" — the as-built
/// SlabAllocator&lt;StagingBlock&gt;). Thread-safe (decode workers Rent, UI/render threads Return). Power-of-two bucket
/// ladder 16KB..16MB; an oversize request gets a plain exact-size array that is never retained. The cap bounds
/// RETAINED (idle) bytes, not in-flight bytes: Rent always succeeds (allocates fresh when the bucket is empty),
/// Return retains only while RetainedBytes stays under the cap and otherwise drops the array for the GC. Unlike
/// ArrayPool&lt;byte&gt;.Shared there are no per-core partitions, so worst-case idle retention is exactly the cap.
/// </summary>
public sealed class PixelBufferPool
{
    public const int MinBucketBytes = 16 * 1024;          // 2^14
    public const int MaxBucketBytes = 16 * 1024 * 1024;   // 2^24 — 2048×2048 BGRA; larger is unpooled
    private const int MinShift = 14;
    private const int BucketCount = 11;                   // 2^14 .. 2^24 inclusive

    public const long DefaultRetainedCapBytes = 32L * 1024 * 1024;

    private readonly Stack<byte[]>[] _buckets = new Stack<byte[]>[BucketCount];
    private readonly long _retainedCapBytes;
    private long _retainedBytes;       // bytes PARKED in _buckets (never in-flight bytes)
    private long _peakRetainedBytes;

    public PixelBufferPool(long retainedCapBytes = DefaultRetainedCapBytes)
    {
        _retainedCapBytes = Math.Max(0, retainedCapBytes);
        for (int i = 0; i < BucketCount; i++) _buckets[i] = new Stack<byte[]>();
    }

    public long RetainedCapBytes => _retainedCapBytes;
    public long RetainedBytes => Interlocked.Read(ref _retainedBytes);
    public long PeakRetainedBytes => Interlocked.Read(ref _peakRetainedBytes);

    /// <summary>Rent ≥ minBytes. ALWAYS succeeds: bucket hit pops the parked array (zero-alloc), miss allocates a
    /// fresh bucket-sized array, oversize allocates exact-size unpooled. Caller returns exactly once.</summary>
    public byte[] Rent(int minBytes)
    {
        if (minBytes > MaxBucketBytes)
            return GC.AllocateUninitializedArray<byte>(minBytes);   // oversize valve: unpooled, Return drops it
        uint size = BitOperations.RoundUpToPowerOf2((uint)Math.Max(minBytes, MinBucketBytes));
        var stack = _buckets[BitOperations.Log2(size) - MinShift];
        lock (stack)
        {
            if (stack.Count > 0)
            {
                byte[] parked = stack.Pop();
                Interlocked.Add(ref _retainedBytes, -parked.Length);
                return parked;
            }
        }
        return GC.AllocateUninitializedArray<byte>((int)size);
    }

    /// <summary>Return a rented buffer. Retains only while the retained total stays under the cap; otherwise (or
    /// for an oversize/foreign array) drops it for the GC. Never blocks on budget.</summary>
    public void Return(byte[] buffer)
    {
        int len = buffer.Length;
        if (len < MinBucketBytes || len > MaxBucketBytes || !BitOperations.IsPow2((uint)len))
            return;   // oversize or not a bucket size → GC reclaims it
        long cur = Interlocked.Read(ref _retainedBytes);      // reserve BEFORE parking so cap is never exceeded
        while (true)
        {
            long after = cur + len;
            if (after > _retainedCapBytes) return;            // over budget → drop for GC
            long prev = Interlocked.CompareExchange(ref _retainedBytes, after, cur);
            if (prev == cur) { UpdatePeak(after); break; }
            cur = prev;
        }
        var stack = _buckets[BitOperations.Log2((uint)len) - MinShift];
        lock (stack)
        {
            AssertNotDoubleReturned(stack, buffer);
            stack.Push(buffer);
        }
    }

    /// <summary>Idle-cadence trim (AppHost.MaybeTrimOnIdle, ~30s): release every parked array to the GC.</summary>
    public void Trim()
    {
        for (int i = 0; i < BucketCount; i++)
        {
            var stack = _buckets[i];
            lock (stack)
            {
                while (stack.Count > 0)
                    Interlocked.Add(ref _retainedBytes, -stack.Pop().Length);
                stack.TrimExcess();
            }
        }
    }

    private void UpdatePeak(long candidate)
    {
        long peak = Interlocked.Read(ref _peakRetainedBytes);
        while (candidate > peak)
        {
            long prev = Interlocked.CompareExchange(ref _peakRetainedBytes, candidate, peak);
            if (prev == peak) return;
            peak = prev;
        }
    }

    // Erased in Release like the D3D12Device confinement tripwires: a double-Return hands one array to two renters.
    [System.Diagnostics.Conditional("FGGUARD")]
    private static void AssertNotDoubleReturned(Stack<byte[]> stack, byte[] buffer)
    {
        foreach (var parked in stack)
            if (ReferenceEquals(parked, buffer))
                throw new InvalidOperationException("PixelBufferPool: buffer returned twice");
    }
}
