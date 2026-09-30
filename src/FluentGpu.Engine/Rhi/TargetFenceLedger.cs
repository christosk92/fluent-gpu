namespace FluentGpu.Rhi;

/// <summary>Phase 1 (detached-window-render-isolation-implementation.md §3.6): the pure decision behind per-target
/// back-buffer fence waits, extracted so it is unit-/gate-testable without a D3D12 device. Each swapchain target owns
/// one ledger — two targets presenting every turn never wait on each other's fence value (INCIDENT 2026-09 §1.6's
/// lockstep coupling is closed by CONSTRUCTION: independent arrays, never a shared index).</summary>
public sealed class TargetFenceLedger
{
    private readonly ulong[] _perIndex;
    public ulong LastSubmit { get; private set; }

    public TargetFenceLedger(int frameCount) => _perIndex = new ulong[frameCount];

    /// <summary>The fence value a submit reusing back-buffer <paramref name="backBufferIndex"/> must wait to reach
    /// (0 = nothing in flight for that index yet).</summary>
    public ulong WaitValueFor(int backBufferIndex) => _perIndex[backBufferIndex];

    /// <summary>Record that the submit which just signalled <paramref name="fence"/> used back-buffer
    /// <paramref name="backBufferIndex"/>.</summary>
    public void Stamp(int backBufferIndex, ulong fence)
    {
        _perIndex[backBufferIndex] = fence;
        LastSubmit = fence;
    }
}

/// <summary>The device-level <c>SubmissionRing</c> slot for submission <paramref name="submitCounter"/> — CPU-written
/// banks (allocators, UploadArena, glyph/pipe instance banks) are keyed by SUBMISSION, not by back-buffer index, so two
/// targets presenting every turn are <paramref name="depth"/> submissions apart on the same slot instead of sharing one
/// index in lockstep (INCIDENT 2026-09 §1.6, item 2).</summary>
public static class SubmissionRingPolicy
{
    public static int Slot(ulong submitCounter, int depth) => (int)(submitCounter % (ulong)depth);
}
