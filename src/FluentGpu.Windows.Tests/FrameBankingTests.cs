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
/// pacing gates depend on it keeping the historical latency-1 contract (present predicted at
/// <c>FrameQpc + 2 · refresh</c>) while the real backend moves to 2.</para>
/// </summary>
public sealed class FrameBankingTests
{
    [Fact]
    public void FrameBanksMatchFrameCount()
    {
        Assert.Equal(3u, D3D12Device.FRAME_COUNT);
        Assert.Equal(D3D12Device.FRAME_COUNT - 1, D3D12Device.MAX_FRAME_LATENCY);
        Assert.Equal((int)D3D12Device.FRAME_COUNT, D3D12Device.FrameBankDepth);
        Assert.Equal(D3D12Device.FrameBankDepth, new OpacityLayerCompositor().TimestampBankCount);
        // AcrylicCompositor: slot 0 = canvas, then one bank of MaxPool (= 12) pool SRVs per frame-in-flight.
        // The 12 is spelled out deliberately — MaxPool is private, and a change to EITHER factor should be a
        // deliberate edit here, not a silently absorbed one.
        Assert.Equal(1 + D3D12Device.FrameBankDepth * 12, AcrylicCompositor.SrvHeapDescriptorCount);
        var baked = new BakedBlurCompositor();
        Assert.Equal(D3D12Device.FrameBankDepth, baked.ScratchBankCount);
        Assert.Equal(D3D12Device.FrameBankDepth, baked.TimestampBankCount);
        // Through the INTERFACE deliberately: IGpuDevice.MaxFrameLatency is a default member and
        // HeadlessGpuDevice does not re-declare it — taking the seam default is exactly what is under test.
        Assert.Equal(1, ((IGpuDevice)new HeadlessGpuDevice()).MaxFrameLatency);   // headless keeps the +2·refresh gate contract
    }
}
