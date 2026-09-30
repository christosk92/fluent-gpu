using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using FluentGpu.Foundation;
using FluentGpu.Rhi;
using FluentGpu.Hosting.Threading;
using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Per-target recording + present state (docs/plans/detached-window-render-isolation-implementation.md §3.1). Render-
/// owner-confined (<c>ThreadGuard.AssertRenderOwner</c>): only the render thread — or the UI thread holding the loop
/// parked/joined (<c>RenderThread.Quiesce…Resume</c> / <c>Dispose</c>) — may touch it. NEVER copied into device
/// working fields the way the retired <c>Activate</c>/<c>StoreActive</c> did (INCIDENT 2026-09 §1.2/§1.3): a
/// <see cref="D3D12Device"/> submit takes a reference to its target's <see cref="TargetFrameState"/> for the
/// duration of the call (<c>D3D12Device.BeginTargetFrame</c>/<c>EndTargetFrame</c>) and drops it again, so a stray
/// post-call touch faults loudly in Debug instead of silently reading another target's state.
/// </summary>
internal sealed unsafe class TargetFrameState
{
    internal readonly D3D12Swapchain Target;
    internal TargetFrameState(D3D12Swapchain target)
    {
        Target = target;
    }

    // ── GPU side ─────────────────────────────────────────────────────────────────────────────────────────────────
    internal ID3D12GraphicsCommandList* List;                                    // ONE command list per target
    internal void* List4;                                                        // the same object as ID3D12GraphicsCommandList4 (render passes)
    internal readonly ulong[] FenceValues = new ulong[D3D12Device.FRAME_COUNT];  // fence value of the last submit that used back buffer k
    internal ulong LastSubmitFence;                                              // max(FenceValues): the stamp Resize/teardown waits on
    internal uint FrameIndex;                                                    // GetCurrentBackBufferIndex() at submit entry

    // ── Tier-3 stencil path clip (moved from D3D12Swapchain.StencilDsv* + D3D12Device._stencil*) ───────────────────
    internal ID3D12Resource* StencilDsv;
    internal ID3D12DescriptorHeap* DsvHeap;
    internal int StencilW, StencilH;
    internal int StencilDepth;
    internal readonly List<bool> StencilScopeMasked = new(8);
    internal bool StencilDsvBound;

    // Volatile: read from the UI thread (IGpuDevice.TextRepaintPending) — "this frame was not faithful, repaint fully".
    internal volatile bool TextRepaintPending;

    // ── present side (moved from D3D12Device) ────────────────────────────────────────────────────────────────────
    internal bool OccludedLatched, LastPresentStoodDown, SkipLatencyOnce, SkipVsyncOnce, HintSettlePresent;
    internal double LastFenceWaitMs, LastLatencyWaitMs;
    internal PresentStats LastPresentStats;
    // Attested statistics (compositor-scroll plan §5.3): after every Present that actually ran on the primary target,
    // SamplePresentStats feeds the ledger DXGI's PAIRED PresentCount/PresentRefreshCount, the present's id and the idle
    // vblanks since the previous submit (LastSubmitQpc). Render-written; the UI reads the ledger's cumulative totals
    // through ISwapchain.PresentsDisplayed/PresentsDropped/VblanksRepeated.
    internal PresentStatisticsLedger PresentLedger;
    internal long LastSubmitQpc;
    internal long LastDwmSampleQpc;
    // Identity of the latest DWM timing sample (PresentStats.DwmSampleSeq): +1 per fresh, baselined 1 Hz sample; 0 = none.
    internal uint DwmSampleSeq;
    internal ulong DwmFramesDropped, DwmFramesMissed, DwmFramesLate;
    internal bool DwmBaselined;   // the DWM counters are only meaningful as deltas — the first sample is a baseline, not data
}
