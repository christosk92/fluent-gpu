namespace FluentGpu.Rhi;

/// <summary>
/// Pure decision extracted from <c>ImageTextureStore</c>'s descriptor-slot quarantine (CANDIDATE FIX, INCIDENT
/// 2026-09: defensive hardening alongside the fence-gated retire path — see <c>ImageTextureStore._slotQuarantine</c>
/// for the real GPU-resource-owning implementation, which cannot run headlessly). A freed SRV/DSV-table slot is not
/// handed back to a free list the instant its owning resource's retire fence clears; it sits for
/// <see cref="FluentGpu.Hosting.Threading.QuarantinePolicy.Quarantine"/> more render-thread "generations" (each one
/// a <c>ReclaimCompleted</c> call) first — the same belt-and-suspenders slack <c>PathRealizationCache</c> and
/// <c>AudioGraphHost</c> already apply to their own consume-gated resources. The countdown arithmetic needs no GPU
/// state, so it is tested here directly instead of through the D3D12-backed store.
/// </summary>
public static class SlotQuarantinePolicy
{
    /// <summary>One generation elapsed for a quarantined slot. Returns the slot's next `generationsLeft` and whether
    /// it is now free to reuse (⇔ the returned value would be ≤ 0). Mirrors <c>ImageTextureStore.AdvanceSlotQuarantine</c>'s
    /// per-entry step exactly, so a caller updates one entry with one call: <c>(left, ready) = Advance(left)</c>.</summary>
    public static (int GenerationsLeft, bool Ready) Advance(int generationsLeft)
    {
        int next = generationsLeft - 1;
        return (next, next <= 0);
    }

    /// <summary>The initial quarantine depth a freshly-retired slot starts at — always the engine's own derived
    /// <see cref="FluentGpu.Hosting.Threading.QuarantinePolicy.Quarantine"/>, never a bare literal (same discipline
    /// that constant's own static ctor guard enforces for its callers).</summary>
    public static int InitialGenerations => FluentGpu.Hosting.Threading.QuarantinePolicy.Quarantine;
}
