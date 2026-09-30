namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The device's CPU-written per-submission banks — the command allocator, the shared <c>UploadArena</c> bank, every
/// geometry/image pipeline's instance bank, the glyph instance bank, and the timestamp query banks — keyed by a
/// SUBMISSION counter, not by a back-buffer index (docs/plans/detached-window-render-isolation-implementation.md
/// §3.2). Slot reuse waits on the fence value of the submit that last used that slot (whichever target it was), so
/// two targets presenting every turn are <see cref="Depth"/> submissions apart on the same slot instead of sharing
/// one back-buffer index in lockstep (the CPU/GPU tear INCIDENT 2026-09 traced to the shared, per-target-activated
/// working fields).
/// <para><b>Depth MUST equal <see cref="D3D12Device.FrameBankDepth"/>.</b> Every bank the slot selects (the compositors'
/// SRV/query banks, the glyph and pipeline instance banks, the upload arena, the baked-blur scratch) is sized to
/// FrameBankDepth and indexed <c>slot % FrameBankDepth</c>. The fence wait guards only the submit that last used THIS
/// ring slot, so a deeper ring would reuse a bank one submission before its GPU work is proven done — a silent CPU/GPU
/// tear. A depth of 4 (the plan's §10.2 option) is a separate change that widens every bank with it;
/// <c>FrameBankingTests</c> pins the equality.</para>
/// </summary>
internal sealed unsafe class SubmissionRing
{
    internal const int Depth = D3D12Device.FrameBankDepth;

    internal readonly TerraFX.Interop.DirectX.ID3D12CommandAllocator*[] Allocators =
        new TerraFX.Interop.DirectX.ID3D12CommandAllocator*[Depth];   // moved from D3D12Device._allocators
    internal readonly ulong[] Fence = new ulong[Depth];                // fence value signalled by the submit that last used slot k
    internal readonly D3D12Swapchain?[] Owner = new D3D12Swapchain?[Depth];   // timestamp attribution (replaces _gpuExecutionTsOwner/_gpuTsOwner)
    internal readonly ulong[] OwnerSubmit = new ulong[Depth];         // replaces _gpuExecutionTsOwnerSubmit
    internal readonly bool[] ExecTsPending = new bool[Depth];
    // Pass-granular GPU timeline (D3D12Device.GpuPassTimingEnabled): the per-interval tags recorded at record time — a
    // CPU-written bank exactly like the allocator/upload-arena banks above, so it keys off the submission slot too and
    // is read back only after that slot's fence wait proves the resolved timestamps retired. PassOwner is kept apart
    // from Owner (which the always-on execution timer consumes and clears first).
    internal readonly FluentGpu.Rhi.GpuPassKind[][] PassKind = CreateBank<FluentGpu.Rhi.GpuPassKind>();
    internal readonly int[][] PassW = CreateBank<int>(), PassH = CreateBank<int>();
    internal readonly int[] PassCount = new int[Depth], PassDropped = new int[Depth], PassBackBufferTransitions = new int[Depth];
    internal readonly bool[] PassPending = new bool[Depth];
    internal readonly D3D12Swapchain?[] PassOwner = new D3D12Swapchain?[Depth];

    internal ulong SubmitCounter;
    internal int NextSlot => (int)(SubmitCounter % (ulong)Depth);

    internal void Stamp(int slot, ulong fence, D3D12Swapchain owner, ulong ownerSubmit)
    {
        Fence[slot] = fence;
        Owner[slot] = owner;
        OwnerSubmit[slot] = ownerSubmit;
        SubmitCounter++;
    }

    private static T[][] CreateBank<T>()
    {
        var a = new T[Depth][];
        for (int i = 0; i < a.Length; i++) a[i] = new T[FluentGpu.Rhi.GpuPassTimeline.MaxPasses];
        return a;
    }
}
