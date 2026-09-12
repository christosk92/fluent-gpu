using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Fence/generation policy, independent of COM so admission and reuse are behaviorally testable.</summary>
internal sealed class SmallImagePageState
{
    private readonly int[] _generation;
    private readonly bool[] _leased;
    private readonly Stack<int> _free;
    public ulong ActivationFence { get; private set; }
    public bool Ready { get; private set; }
    public int LiveCount { get; private set; }
    public int Capacity => _generation.Length;
    public SmallImagePageState(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _generation = new int[capacity]; _leased = new bool[capacity];
        _free = new Stack<int>(capacity);
        for (int i = capacity - 1; i >= 0; i--) _free.Push(i);
    }
    public void SubmitActivation(ulong fence)
    {
        if (fence == 0 || fence == ulong.MaxValue || ActivationFence != 0)
            throw new InvalidOperationException("Activation must be submitted exactly once with a real fence.");
        ActivationFence = fence;
    }
    public void Complete(ulong completed)
    {
        if (completed != ulong.MaxValue && ActivationFence != 0 && completed >= ActivationFence) Ready = true;
    }
    public bool TryAcquire(out int index, out int generation)
    {
        index = -1; generation = 0;
        if (!Ready || _free.Count == 0) return false;
        index = _free.Pop();
        generation = unchecked(++_generation[index]);
        if (generation == 0) generation = ++_generation[index];
        _leased[index] = true; LiveCount++;
        return true;
    }
    public bool Release(int index, int generation, ulong lastUse, ulong completed)
    {
        if (completed == ulong.MaxValue || completed < lastUse || (uint)index >= (uint)Capacity ||
            !_leased[index] || _generation[index] != generation) return false;
        _leased[index] = false; LiveCount--; _free.Push(index);
        return true;
    }
}

/// <summary>
/// Independent UNKNOWN-layout UMA textures placed at device-queried offsets. Pages are activated in one cold batch;
/// CPU writes are forbidden until that submission completes. The heap owns resources; a lease owns only a fenced
/// right to write/use one resource. There are no queue waits, resource creates or managed allocations on warm acquire.
/// </summary>
internal sealed unsafe class SmallImageHeapPool : IDisposable
{
    internal const ulong FirstPageBytes = 128 * 1024, GrowthPageBytes = 1024 * 1024, MaxHeapBytes = 16 * 1024 * 1024;
    internal sealed class Page
    {
        internal ID3D12Heap* Heap;
        internal readonly nint[] Resources;
        internal readonly SmallImagePageState State;
        internal readonly int Bucket;
        internal readonly ulong Bytes, RequiredEach;
        internal Page(int bucket, ulong bytes, ulong required, int count)
        { Bucket = bucket; Bytes = bytes; RequiredEach = required; Resources = new nint[count]; State = new(count); }
    }
    internal readonly struct Lease
    {
        internal readonly Page? Owner;
        internal readonly int Index, Generation;
        internal Lease(Page page, int index, int generation) { Owner = page; Index = index; Generation = generation; }
        internal bool IsValid => Owner is not null;
        internal ID3D12Resource* Resource => Owner is null ? null : (ID3D12Resource*)Owner.Resources[Index];
    }
    private readonly ID3D12Device* _device;
    private readonly List<Page> _pages = new(16);
    private readonly bool[] _queried = new bool[2], _disabled = new bool[2];
    private readonly ulong[] _required = new ulong[2];
    private ulong _heapBytes, _occupiedBytes;
    private int _unsubmitted;
    internal ulong HeapBytes => System.Threading.Volatile.Read(ref _heapBytes);
    internal ulong OccupiedBytes => System.Threading.Volatile.Read(ref _occupiedBytes); // subset of heap bytes, NEVER added to committed totals
    internal int ResourceCreateCount { get; private set; }
    internal int PageCount => _pages.Count;
    internal bool HasUnsubmittedActivation => _unsubmitted != 0;
    internal SmallImageHeapPool(ID3D12Device* device) { _device = device; }
    internal static bool CanReserve(ulong residentIncludingPending, ulong pageBytes)
        => (pageBytes == FirstPageBytes || pageBytes == GrowthPageBytes) && residentIncludingPending <= MaxHeapBytes - pageBytes;

    internal static bool TryLayout(ulong size, ulong alignment, ulong defaultSize, ulong pageBytes, out int count)
    {
        count = 0;
        if (alignment != 4096 || size == 0 || size > pageBytes || size % alignment != 0 ||
            defaultSize == 0 || defaultSize > long.MaxValue || size >= defaultSize ||
            pageBytes % 65536 != 0 || pageBytes > GrowthPageBytes) return false;
        count = checked((int)(pageBytes / size));
        return count > 0;
    }

    internal bool TryAcquire(int bucket, out Lease lease)
    {
        lease = default;
        int b = bucket == 64 ? 0 : bucket == 128 ? 1 : -1;
        if (b < 0 || _disabled[b]) return false;
        if (!_queried[b] && !Query(bucket, b)) return false;
        bool pending = false, hasPage = false;
        foreach (var page in _pages)
        {
            if (page.Bucket != bucket) continue;
            hasPage = true;
            if (!page.State.Ready) pending = true;
            if (page.State.TryAcquire(out int index, out int generation))
            {
                lease = new(page, index, generation); _occupiedBytes += page.RequiredEach;
                return true;
            }
        }
        // This upload takes the committed fallback. A subsequent batch can use the asynchronously activated page.
        ulong bytes = hasPage ? GrowthPageBytes : FirstPageBytes;
        if (!pending && CanReserve(_heapBytes, bytes)) CreatePage(bucket, b, bytes);
        return false;
    }

    private bool Query(int bucket, int b)
    {
        _queried[b] = true;
        var desc = Description(bucket); desc.Alignment = 4096;
        var small = _device->GetResourceAllocationInfo(0, 1, &desc);
        desc.Alignment = 0;
        var normal = _device->GetResourceAllocationInfo(0, 1, &desc);
        bool usable = TryLayout(small.SizeInBytes, small.Alignment, normal.SizeInBytes, FirstPageBytes, out _);
        Diag.Line($"[d3d12] small-image bucket={bucket} required={small.SizeInBytes} alignment={small.Alignment} default={normal.SizeInBytes} eligible={usable}");
        if (!usable) { _disabled[b] = true; return false; }
        _required[b] = small.SizeInBytes;
        return true;
    }

    private void CreatePage(int bucket, int b, ulong bytes)
    {
        int count = checked((int)(bytes / _required[b]));
        var page = new Page(bucket, bytes, _required[b], count);
        D3D12_HEAP_DESC hd = default;
        hd.SizeInBytes = bytes; hd.Alignment = 65536;
        hd.Properties.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM;
        hd.Properties.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY.D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
        hd.Properties.MemoryPoolPreference = D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0;
        hd.Flags = D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES;
        ID3D12Heap* heap = null;
        int hr = (int)_device->CreateHeap(&hd, __uuidof<ID3D12Heap>(), (void**)&heap);
        if (hr < 0 || heap == null) { Disable(b, "CreateHeap", hr); return; }
        page.Heap = heap;
        D3D12MemoryDiagnostics.Track(heap, "Image.PlacedHeap", bytes);
        var desc = Description(bucket); desc.Alignment = 4096;
        for (int i = 0; i < count; i++)
        {
            ID3D12Resource* resource = null;
            hr = (int)_device->CreatePlacedResource(heap, checked((ulong)i * page.RequiredEach), &desc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null, __uuidof<ID3D12Resource>(), (void**)&resource);
            if (hr < 0 || resource == null)
            {
                Disable(b, "CreatePlacedResource", hr);
                Destroy(page); // never submitted: immediate rollback is safe
                return;
            }
            page.Resources[i] = (nint)resource;
            D3D12MemoryDiagnostics.Track(resource, "Image.PlacedSlot", 0); // heap is the sole allocation owner
            ResourceCreateCount++;
        }
        _pages.Add(page); _heapBytes += bytes; _unsubmitted++;
    }

    internal void RecordActivations(ID3D12GraphicsCommandList* cmd, ulong submitFence)
    {
        if (_unsubmitted == 0) return;
        foreach (var page in _pages)
        {
            if (page.State.ActivationFence != 0) continue;
            foreach (nint pointer in page.Resources)
            {
                D3D12_RESOURCE_BARRIER barrier = default;
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_ALIASING;
                barrier.Anonymous.Aliasing.pResourceAfter = (ID3D12Resource*)pointer;
                cmd->ResourceBarrier(1, &barrier);
            }
            page.State.SubmitActivation(submitFence); _unsubmitted--;
        }
    }

    /// <summary>CPU-only maintenance: no command recording and no wait. Submitted activation alone never requests frames.</summary>
    internal void Reclaim(ulong completedFence)
    {
        if (completedFence == ulong.MaxValue) return; // device removal is NOT completion
        foreach (var page in _pages) page.State.Complete(completedFence);
        bool warm64 = false, warm128 = false;
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            if (!page.State.Ready || page.State.LiveCount != 0) continue;
            int b = page.Bucket == 64 ? 0 : 1;
            bool keep = !_disabled[b] && !(b == 0 ? warm64 : warm128);
            if (keep) { if (b == 0) warm64 = true; else warm128 = true; continue; }
            Destroy(page); _heapBytes -= page.Bytes; _pages.RemoveAt(i--);
        }
    }

    internal bool Release(in Lease lease, ulong lastUse, ulong completed)
    {
        var page = lease.Owner;
        if (page is null || !page.State.Release(lease.Index, lease.Generation, lastUse, completed)) return false;
        _occupiedBytes -= page.RequiredEach;
        return true;
    }
    internal void WriteFailed(in Lease lease, string stage, int hr)
    { if (lease.Owner is not null) Disable(lease.Owner.Bucket == 64 ? 0 : 1, stage, hr); }
    private void Disable(int b, string stage, int hr)
    {
        if (!_disabled[b]) Diag.Line($"[d3d12] small-image bucket={(b == 0 ? 64 : 128)} disabled stage={stage} hr=0x{hr:X8}; committed fallback");
        _disabled[b] = true;
    }
    private static D3D12_RESOURCE_DESC Description(int bucket) => new()
    {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Width = (ulong)bucket, Height = (uint)bucket, DepthOrArraySize = 1, MipLevels = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc = new() { Count = 1 }, Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN
    };
    private static void Destroy(Page page)
    {
        for (int i = 0; i < page.Resources.Length; i++)
        {
            var resource = (ID3D12Resource*)page.Resources[i];
            if (resource == null) continue;
            D3D12MemoryDiagnostics.Release(resource, "Image.PlacedSlot"); resource->Release(); page.Resources[i] = 0;
        }
        if (page.Heap != null)
        { D3D12MemoryDiagnostics.Release(page.Heap, "Image.PlacedHeap"); page.Heap->Release(); page.Heap = null; }
    }
    public void Dispose()
    {
        // Device/store owner has drained all uses, as for every other image resource at shutdown.
        foreach (var page in _pages) Destroy(page);
        _pages.Clear(); _heapBytes = _occupiedBytes = 0; _unsubmitted = 0;
    }
}
