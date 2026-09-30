using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Gates for the INCIDENT 2026-09 fix set (production crash: a detached video pop-out's first frame threw
/// <c>cmdList.Close failed: 0x80070057</c> from an oversized/stale stencil-clip DSV and killed the process). The
/// GPU-backed pieces (D3D12Device's per-target DSV, ImageTextureStore's slot quarantine, AppHost's detached-child
/// survivability catch) cannot run headlessly — TerraFX enters only through FluentGpu.Windows, which this harness's
/// TerraFX-free transitive closure never references (README.md / CLAUDE.md "two invariants" note) — so, per the
/// "no source-text tests" pattern (CLAUDE.md: extract the decision into an engine-free pure class and unit-test
/// that), the DECISIONS behind CANDIDATE 1 and CANDIDATE 2 were extracted into <see cref="StencilDsvPolicy"/> and
/// <see cref="SlotQuarantinePolicy"/> (both <c>FluentGpu.Engine/Rhi/</c>) and are gated here directly.
/// </summary>
static class DetachedRenderResilienceSuite
{
    public static void Run(StringTable strings)
    {
        StencilDsvPolicyChecks();
        SlotQuarantinePolicyChecks();
        RecordedOpRingChecks();
        TargetFenceLedgerChecks();
    }

    // gate.d3d12.forensic.* (Phase 0 §2.6): the always-on forensic ring is the only evidence a shipping (Release)
    // crash leaves — the debug layer is #if DEBUG + AppOptions.D3D12DebugLayer and never runs in the field. Gated directly since
    // RecordedOpRing is a pure, TerraFX-free POD type in FluentGpu.Engine/Rhi (no D3D12Device needed headlessly).
    static void RecordedOpRingChecks()
    {
        // gate.d3d12.forensic.ring-keeps-newest-64: push 70 entries (A = 0..69) → only the newest 64 survive.
        var ring = new RecordedOpRing();
        for (uint i = 0; i < 70; i++) ring.Push(RecordedOp.Barrier, target: 1, a: i);
        Check("gate.d3d12.forensic.ring-keeps-newest-64",
            ring.Count == 64 && ring.At(0).A == 6 && ring.At(63).A == 69 && ring.Total == 70,
            $"Count={ring.Count} At(0).A={ring.At(0).A} At(63).A={ring.At(63).A} Total={ring.Total}");

        // gate.d3d12.forensic.ring-format-names-target: the forensic line must name which target each op ran
        // against — a mismatch between the failing target and the last op's target IS the INCIDENT 2026-09 signature.
        var fmtRing = new RecordedOpRing();
        fmtRing.Push(RecordedOp.SetRenderTargetWithDsv, target: 2);
        fmtRing.Push(RecordedOp.ListClose, target: 1);
        var sb = new System.Text.StringBuilder();
        fmtRing.Format(sb);
        string formatted = sb.ToString();
        int idxSrt = formatted.IndexOf("SetRenderTargetWithDsv#2", StringComparison.Ordinal);
        int idxClose = formatted.IndexOf("ListClose#1", StringComparison.Ordinal);
        Check("gate.d3d12.forensic.ring-format-names-target",
            idxSrt >= 0 && idxClose > idxSrt, formatted);

        // gate.d3d12.forensic.push-is-zero-alloc: Push is a struct write into a preallocated array — the existing
        // zero-alloc tripwire idiom (GC.GetAllocatedBytesForCurrentThread() delta across the hot loop).
        var allocRing = new RecordedOpRing();
        allocRing.Push(RecordedOp.Barrier, target: 1); // first call can pay for lazy one-time JIT/statics
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) allocRing.Push(RecordedOp.Barrier, target: 1, a: (uint)i, b: (uint)i);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.d3d12.forensic.push-is-zero-alloc", bytes == 0, $"{bytes} bytes / 10000 pushes");
    }

    // gate.render.ledger.* (Phase 1 §3.6): TargetFenceLedger/SubmissionRingPolicy are the pure decisions behind
    // per-target back-buffer fence waits and the device-level submission-ring slot — extracted so INCIDENT 2026-09
    // §1.6's lockstep coupling (two targets presenting every turn sharing one frame index) is gated without a D3D12
    // device.
    static void TargetFenceLedgerChecks()
    {
        // gate.render.ledger.own-slots-only: two independent targets' ledgers never see each other's stamps.
        var a = new TargetFenceLedger(frameCount: 3);
        var b = new TargetFenceLedger(frameCount: 3);
        a.Stamp(1, 7);
        Check("gate.render.ledger.own-slots-only",
            b.WaitValueFor(1) == 0 && a.WaitValueFor(1) == 7 && a.LastSubmit == 7,
            $"b.WaitValueFor(1)={b.WaitValueFor(1)} a.WaitValueFor(1)={a.WaitValueFor(1)} a.LastSubmit={a.LastSubmit}");

        // gate.render.ledger.ring-slot-reuse-distance: a submission-ring slot is reused exactly `depth` submissions
        // later, never one submission later (the lockstep coupling INCIDENT 2026-09 §1.6 exploited).
        bool reuseOk = true;
        for (ulong n = 0; n < 8; n++)
        {
            int slotN = SubmissionRingPolicy.Slot(n, 3);
            if (SubmissionRingPolicy.Slot(n + 3, 3) != slotN || SubmissionRingPolicy.Slot(n + 1, 3) == slotN)
            { reuseOk = false; break; }
        }
        Check("gate.render.ledger.ring-slot-reuse-distance", reuseOk);
    }

    // gate.d3d12.dsv-per-target (headless-equivalent): the cached stencil DSV is reused when it COVERS the request
    // and recreated only when the target grew. The incident (a main-window DSV bound with a detached child's smaller
    // target) is closed by D3D12Swapchain.StencilDsv* making the cache PER-TARGET (D3D12Device.Activate/StoreActive),
    // not by this comparison; an exact-match comparison here was itself a bug (a per-frame WaitForGpu, see below).
    static void StencilDsvPolicyChecks()
    {
        // The DSV is PER TARGET (D3D12Swapchain.StencilDsv*), so the incident shape — one window's oversized DSV
        // bound with another window's small target — is closed by STORAGE, not by this policy. On the SAME target an
        // oversized DSV is a valid reuse, and must be: EnsureStencilDsv sizes it to max(active render target,
        // swapchain), and an acrylic canvas / blur RT is routinely larger than the window. Demanding an exact match
        // here recreated that DSV behind WaitForGpu() on every submit (2026-09-22).
        Check("gate.d3d12.dsv-per-target: an oversized DSV on the same target covers a smaller request (no per-frame recreate)",
            StencilDsvPolicy.Covers(currentW: 1920, currentH: 1080, requestedW: 480, requestedH: 270)
            && !StencilDsvPolicy.NeedsRecreate(currentW: 1920, currentH: 1080, requestedW: 480, requestedH: 270));
        Check("gate.d3d12.dsv-per-target: an undersized cached DSV does not cover a larger request and needs a recreate",
            !StencilDsvPolicy.Covers(currentW: 480, currentH: 270, requestedW: 1920, requestedH: 1080)
            && StencilDsvPolicy.NeedsRecreate(currentW: 480, currentH: 270, requestedW: 1920, requestedH: 1080));
        Check("gate.d3d12.dsv-per-target: one axis too small is too small",
            StencilDsvPolicy.NeedsRecreate(currentW: 1920, currentH: 270, requestedW: 480, requestedH: 1080));
        Check("gate.d3d12.dsv-per-target: identical size covers and needs no recreate",
            StencilDsvPolicy.Covers(currentW: 480, currentH: 270, requestedW: 480, requestedH: 270)
            && !StencilDsvPolicy.NeedsRecreate(currentW: 480, currentH: 270, requestedW: 480, requestedH: 270));
        // Degenerate but reachable (a target mid-teardown / not yet laid out): zero on either axis never covers.
        Check("gate.d3d12.dsv-per-target: a zero-sized cached DSV never covers a live request",
            !StencilDsvPolicy.Covers(currentW: 0, currentH: 0, requestedW: 480, requestedH: 270));
    }

    // gate.image.retire-fence-at-free (defensive slot quarantine, CANDIDATE 2): the pure countdown ImageTextureStore
    // drives its per-slot `_slotQuarantine` list with — SlotQuarantinePolicy.Advance. Proves the depth is DERIVED
    // from QuarantinePolicy.RenderInFlightDepth (never a bare literal — the same discipline QuarantinePolicy's own
    // static-ctor guard enforces) and that a slot is never marked ready before exactly that many generations elapse.
    static void SlotQuarantinePolicyChecks()
    {
        Check("gate.image.retire-fence-at-free: initial quarantine depth is derived from RenderInFlightDepth+1, not a literal",
            SlotQuarantinePolicy.InitialGenerations == QuarantinePolicy.RenderInFlightDepth + 1);

        // Walk a slot's whole lifecycle through the SAME loop ImageTextureStore.AdvanceSlotQuarantine runs, and
        // record exactly which generation it becomes ready on.
        int left = SlotQuarantinePolicy.InitialGenerations;
        int generationsElapsed = 0;
        bool ready = false;
        while (!ready && generationsElapsed < 64)   // 64: any real deadlock/miscount shows up in single digits
        {
            (left, ready) = SlotQuarantinePolicy.Advance(left);
            generationsElapsed++;
        }
        Check("gate.image.retire-fence-at-free: a quarantined slot becomes ready after exactly InitialGenerations generations",
            ready && generationsElapsed == SlotQuarantinePolicy.InitialGenerations,
            $"generationsElapsed={generationsElapsed}, expected={SlotQuarantinePolicy.InitialGenerations}");

        // The generation BEFORE the last one must NOT be ready yet — this is the actual bug class the quarantine
        // guards against (a slot reused one generation too early). Re-walk and check the second-to-last step.
        left = SlotQuarantinePolicy.InitialGenerations;
        bool readyTooEarly = false;
        for (int i = 0; i < SlotQuarantinePolicy.InitialGenerations - 1; i++)
        {
            (left, readyTooEarly) = SlotQuarantinePolicy.Advance(left);
        }
        Check("gate.image.retire-fence-at-free: a slot is NOT ready one generation before its quarantine elapses",
            !readyTooEarly);

        // Simulate ImageTextureStore.AdvanceSlotQuarantine's actual container loop (List<(slot, left)>) end to end,
        // including a slot queued LATER than another — quarantine tracks each slot's OWN countdown independently,
        // not a single shared clock, so an interleaved free pattern (evict A, evict B one generation later) must not
        // let B jump the queue.
        var quarantine = new List<(int Slot, int Left)> { (100, SlotQuarantinePolicy.InitialGenerations) };
        var freed = new List<int>();
        AdvanceAll(quarantine, freed);                                  // gen 1 for slot 100
        quarantine.Add((200, SlotQuarantinePolicy.InitialGenerations)); // slot 200 freed one generation later
        AdvanceAll(quarantine, freed);                                  // gen 2 for 100 (ready), gen 1 for 200
        Check("gate.image.retire-fence-at-free: slot 100 (freed first) is reusable before slot 200 (freed later)",
            freed.Count == 1 && freed[0] == 100 && quarantine.Count == 1 && quarantine[0].Slot == 200);
        AdvanceAll(quarantine, freed);                                  // gen 2 for 200 (ready)
        Check("gate.image.retire-fence-at-free: slot 200 becomes reusable on ITS OWN generation, not slot 100's",
            freed.Count == 2 && freed[1] == 200 && quarantine.Count == 0);
    }

    // Mirrors ImageTextureStore.AdvanceSlotQuarantine's walk-backwards-and-remove loop exactly (same shape, so this
    // gate is pinned to the real call site's behavior, not a reimplementation that could silently drift from it).
    static void AdvanceAll(List<(int Slot, int Left)> quarantine, List<int> freed)
    {
        for (int i = quarantine.Count - 1; i >= 0; i--)
        {
            var (slot, left) = quarantine[i];
            var (next, ready) = SlotQuarantinePolicy.Advance(left);
            if (ready) { freed.Add(slot); quarantine.RemoveAt(i); }
            else quarantine[i] = (slot, next);
        }
    }
}
