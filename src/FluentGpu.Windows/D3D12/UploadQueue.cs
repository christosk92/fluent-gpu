using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// A side queue for work that must never ride the frame's DIRECT list (docs/plans/scroll-gpu-retained-tiles-implementation.md
/// §C): the COPY queue the discrete image uploads are recorded on, and the COMPUTE queue the baked image blurs run on.
/// One command list over a small ring of allocators; each batch closes, executes and signals its own fence with a
/// strictly increasing value (<see cref="UploadFenceLedger"/>). Readiness of what a batch wrote is a fence COMPARE
/// (<see cref="IsComplete"/>) — the frame never waits on it: until the copy lands the scene draws the placeholder, and
/// the host keeps turning while <see cref="HasInFlight"/>. The CPU waits only when every allocator of the ring still
/// holds an unfinished batch (<see cref="Depth"/> batches in flight — the per-turn upload budget keeps that a
/// non-event). Every call goes through the generated comabi bindings (<see cref="D3D12SideQueues"/> builds the
/// objects); render-thread confined like the rest of the device.
/// </summary>
internal sealed unsafe class UploadQueue : IDisposable
{
    /// <summary>Allocators in the ring = batches that may be in flight at once.</summary>
    public const int Depth = 4;

    private readonly D3D12_COMMAND_LIST_TYPE _type;
    private readonly string _name;
    private void* _queue, _fence, _list;
    private readonly void*[] _allocators = new void*[Depth];
    private readonly ulong[] _allocatorFence = new ulong[Depth];
    private int _open = -1;
    private int _next;
    private UploadFenceLedger _ledger;
    private HANDLE _event;

    public UploadQueue(D3D12_COMMAND_LIST_TYPE type, string name)
    {
        _type = type;
        _name = name;
    }

    /// <summary>Create the queue, its fence, the allocator ring and the (closed) list. Throws on a failed create — a
    /// device that cannot make a side queue cannot upload at all.</summary>
    public void Init(void* device)
    {
        Check(D3D12SideQueues.CreateQueue(device, _type, out _queue), "CreateQueue");
        Check(D3D12SideQueues.CreateFence(device, 0, out _fence), "CreateFence");
        for (int i = 0; i < Depth; i++) Check(D3D12SideQueues.CreateAllocator(device, _type, out _allocators[i]), "CreateAllocator");
        Check(D3D12SideQueues.CreateClosedList(device, _type, _allocators[0], asList4: false, out _list), "CreateCommandList");
        _event = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);
    }

    public ID3D12CommandQueue* Queue => (ID3D12CommandQueue*)_queue;

    /// <summary>True while a batch is being recorded (between <see cref="Open"/> and <see cref="Submit"/>).</summary>
    public bool IsOpen => _open >= 0;

    /// <summary>The recording list, opening a batch on the next allocator of the ring when none is open (resetting it
    /// once its last batch completed — a CPU wait only when all <see cref="Depth"/> are still in flight).</summary>
    public ID3D12GraphicsCommandList* Open()
    {
        if (_open >= 0) return (ID3D12GraphicsCommandList*)_list;
        int a = _next;
        _next = (a + 1) % Depth;
        WaitFor(_allocatorFence[a]);
        Check(Gen.ID3D12CommandAllocatorVtbl.Reset(_allocators[a]), "allocator.Reset");
        Check(Gen.ID3D12GraphicsCommandListVtbl.Reset(_list, _allocators[a], null), "list.Reset");
        _open = a;
        return (ID3D12GraphicsCommandList*)_list;
    }

    /// <summary>The fence value the open batch will signal when submitted (stamp what it writes with this).</summary>
    public ulong PendingValue => _ledger.LastSignaled + 1;

    /// <summary>Close, execute and signal the open batch. Returns its fence value; 0 when no batch was open.</summary>
    public ulong Submit()
    {
        if (_open < 0) return 0;
        Check(Gen.ID3D12GraphicsCommandListVtbl.Close(_list), "list.Close");
        void* l = _list;
        Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, &l);
        ulong v = _ledger.NextSignal();
        Check(Gen.ID3D12CommandQueueVtbl.Signal(_queue, _fence, v), "queue.Signal");
        _allocatorFence[_open] = v;
        _open = -1;
        return v;
    }

    /// <summary>The highest completed batch value (a fresh fence read, folded in monotonically; a removed device reads
    /// as everything complete — nothing on it will ever finish, and nothing may wait for it).</summary>
    public ulong Completed
    {
        get
        {
            _ledger.ObserveCompleted(Gen.ID3D12FenceVtbl.GetCompletedValue(_fence));
            return _ledger.Completed;
        }
    }

    /// <summary>Has the batch that signaled <paramref name="value"/> finished? (0 = nothing staged → true.)</summary>
    public bool IsComplete(ulong value) => value == 0 || value <= _ledger.Completed || value <= Completed;

    /// <summary>Batches submitted and not yet complete.</summary>
    public bool HasInFlight => _ledger.LastSignaled > Completed;

    /// <summary>The same question from ANY thread (the host's wake / skip-submit checks): a plain fence read that never
    /// touches the render thread's ledger.</summary>
    public bool HasInFlightAnyThread
    {
        get
        {
            void* fence = _fence;
            return fence != null && _ledger.LastSignaled > Gen.ID3D12FenceVtbl.GetCompletedValue(fence);
        }
    }

    /// <summary>Block until <paramref name="value"/> completed (0 → immediate). The allocator-ring backstop, device
    /// teardown and recovery.</summary>
    public void WaitFor(ulong value)
    {
        if (value == 0 || IsComplete(value)) return;
        Check(Gen.ID3D12FenceVtbl.SetEventOnCompletion(_fence, value, (void*)_event), "SetEventOnCompletion");
        WaitForSingleObject(_event, INFINITE);
        _ledger.ObserveCompleted(Gen.ID3D12FenceVtbl.GetCompletedValue(_fence));
    }

    /// <summary>Wait for every submitted batch.</summary>
    public void WaitIdle() => WaitFor(_ledger.LastSignaled);

    private void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{_name}.{what} failed: 0x{(uint)hr:X8}");
    }

    public void Dispose()
    {
        if (_queue != null && _fence != null)
        {
            if (_open >= 0) { Gen.ID3D12GraphicsCommandListVtbl.Close(_list); _open = -1; }
            try { WaitIdle(); } catch (InvalidOperationException) { }
        }
        if (_list != null) { Gen.IUnknownVtbl.Release(_list); _list = null; }
        for (int i = 0; i < Depth; i++)
            if (_allocators[i] != null) { Gen.IUnknownVtbl.Release(_allocators[i]); _allocators[i] = null; }
        if (_fence != null) { Gen.IUnknownVtbl.Release(_fence); _fence = null; }
        if (_queue != null) { Gen.IUnknownVtbl.Release(_queue); _queue = null; }
        if (_event != HANDLE.NULL) { CloseHandle(_event); _event = HANDLE.NULL; }
    }
}
