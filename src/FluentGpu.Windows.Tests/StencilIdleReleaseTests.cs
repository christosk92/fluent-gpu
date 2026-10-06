using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The stencil surface is released only when it exists, nothing bound it for the idle window, and the GPU has
/// completed the target's last submit.</summary>
public sealed class StencilIdleReleaseTests
{
    private const long Idle = D3D12Device.StencilIdleReleaseMs;

    [Fact]
    public void ReleasesAnIdleCompletedSurface() => Assert.True(D3D12Device.ShouldReleaseStencil(true, 1_000 + Idle, 1_000, 5, 5));

    [Fact]
    public void KeepsARecentlyUsedSurface() => Assert.False(D3D12Device.ShouldReleaseStencil(true, 1_000 + Idle - 1, 1_000, 5, 5));

    [Fact]
    public void KeepsASurfaceTheGpuMayStillUse() => Assert.False(D3D12Device.ShouldReleaseStencil(true, 1_000 + Idle, 1_000, 6, 5));

    [Fact]
    public void NothingToReleaseWhenAbsent() => Assert.False(D3D12Device.ShouldReleaseStencil(false, 1_000 + Idle, 1_000, 5, 5));
}
