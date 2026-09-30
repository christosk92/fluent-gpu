using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Locks the frame-banking arithmetic that the 3-buffer / max-frame-latency-2 pipelining decision
/// (<c>docs/plans/gpu-robustness-implementation.md</c> WS-C) spread across the backend. The header comment on
/// <c>D3D12Device.FRAME_COUNT</c> used to claim "every site keys off this constant, so 3 is a one-line change";
/// it was FALSE — eight pipelines and three compositors carried independent depth-2 assumptions, the compositors
/// as hidden <c>&amp; 1</c> parity banks that silently corrupt (frame N overwriting the descriptors frame N-1 is
/// still reading) the moment a third frame is in flight.
///
/// <para>So this asserts the DERIVED values, not the source text: the per-frame bank depth every CPU-written GPU
/// bank sizes itself from, the DXGI max-frame-latency argument, the Acrylic SRV heap width, and the Opacity /
/// BakedBlur timestamp + scratch bank counts. Raising <c>FRAME_COUNT</c> without widening one of those banks fails
/// HERE rather than as an intermittent flicker in an acrylic pool on someone's 165 Hz panel.</para>
///
/// <para>Headless and device-free: none of the three compositors declares a constructor — they are
/// field-initializer-only, so <c>new</c> allocates the managed arrays whose lengths are under test and touches no
/// D3D12 device, heap or queue. <see cref="HeadlessGpuDevice"/> is asserted alongside because the deterministic
/// pacing gates depend on it keeping the latency-1 contract (present predicted at <c>FrameQpc + 2 · refresh</c>) —
/// which the D3D12 backend now also reports, its present-queue depth having been decoupled from its 3 frame banks.</para>
/// </summary>
public sealed class FrameBankingTests
{
    [Fact]
    public void FrameBanksMatchFrameCount()
    {
        Assert.Equal(3u, D3D12Device.FRAME_COUNT);
        // NOT FRAME_COUNT - 1. The bank depth (3) is a memory/pipelining decision; the present-queue depth is a
        // LATENCY decision: it starts at 1 and the host deepens it to at most 2 only while measured GPU execution
        // approaches the refresh (PresentQueueDepthPolicy). Literals, so the two cannot be re-coupled by accident — and
        // every queued frame plus the one being recorded needs its own CPU-written bank.
        Assert.Equal(1u, D3D12Device.InitialPresentQueueDepth);
        Assert.Equal(2u, D3D12Device.MaxPresentQueueDepth);
        Assert.True(D3D12Device.FRAME_COUNT >= D3D12Device.MaxPresentQueueDepth + 1);
        Assert.Equal((int)D3D12Device.FRAME_COUNT, D3D12Device.FrameBankDepth);
        // Phase 1 (detached-window-render-isolation §3.6): the CPU-written submission ring's slot depth must track
        // the same bank-depth decision as everything above — it replaced the frame-index-keyed banks 1:1, not a
        // separately chosen constant that could silently drift from FrameBankDepth.
        Assert.Equal(D3D12Device.FrameBankDepth, SubmissionRing.Depth);
        // The image bake runs on its own compute queue: its scratch pyramids and timestamp pairs are banked per compute
        // batch in flight (the side queue's allocator ring), not per frame.
        var baked = new BakedBlurCompositor();
        Assert.Equal(UploadQueue.Depth, baked.ScratchBankCount);
        Assert.Equal(UploadQueue.Depth, baked.TimestampBankCount);
        // Through the INTERFACE deliberately: IGpuDevice.MaxFrameLatency is a default member and
        // HeadlessGpuDevice does not re-declare it — taking the seam default is exactly what is under test.
        Assert.Equal(1, ((IGpuDevice)new HeadlessGpuDevice()).MaxFrameLatency);   // headless keeps the +2·refresh gate contract
    }
}
