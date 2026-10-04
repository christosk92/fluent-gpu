using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// F255: the image-upload drain's per-turn staging budget is a pure function of the tier, the display period and the
/// live-video state. A fixed 2 MiB per present turn is 240 MB/s of CPU-swizzled texture writes on the render thread at
/// 120 Hz; on the weak (UMA / iGPU) tier the budget is quoted per millisecond of display period (1 MiB per 8.33 ms) and
/// halves while a video surface is live, so a cover burst cannot crowd the turns that carry the video placement commit.
/// <c>GpuProfile.IsWeak</c> is false without a real adapter, which is why the tier crosses as an argument.
/// </summary>
public sealed class UploadTurnBudgetTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(8.333)]
    [InlineData(16.667)]
    [InlineData(0.0)]
    public void Discrete_KeepsTheFlatPerTurnBudget_WhateverThePeriodOrVideo(double periodMs)
    {
        Assert.Equal(D3D12Device.UploadBytesPerTurn, UploadTurnBudget.BytesPerTurn(false, periodMs, false));
        Assert.Equal(D3D12Device.UploadBytesPerTurn, UploadTurnBudget.BytesPerTurn(false, periodMs, true));
    }

    [Fact]
    public void Weak_At120Hz_IsOneMiB_AndHalvesWhileVideoIsLive()
    {
        double period = 1000.0 / 120.0;
        Assert.Equal(MiB, UploadTurnBudget.BytesPerTurn(true, period, false));
        Assert.Equal(MiB / 2, UploadTurnBudget.BytesPerTurn(true, period, true));
    }

    [Fact]
    public void Weak_At60Hz_IsTwoMiB_SoTheBytesPerSecondStayTheSame()
    {
        double p60 = 1000.0 / 60.0, p120 = 1000.0 / 120.0;
        int at60 = UploadTurnBudget.BytesPerTurn(true, p60, false);
        int at120 = UploadTurnBudget.BytesPerTurn(true, p120, false);
        Assert.Equal(2 * MiB, at60);
        Assert.Equal(at60 / p60, at120 / p120, 3);   // same bytes per millisecond of wall time
        Assert.Equal(MiB, UploadTurnBudget.BytesPerTurn(true, p60, true));
    }

    [Fact]
    public void Weak_ClampsToTheFloorAndCeiling()
    {
        // A 360 Hz panel with video live would be ~0.17 MiB per turn: floored so the drain still makes progress.
        Assert.Equal(UploadTurnBudget.WeakMinBytes, UploadTurnBudget.BytesPerTurn(true, 1000.0 / 360.0, true));
        // A 24 Hz period would be ~5 MiB: capped.
        Assert.Equal(UploadTurnBudget.WeakMaxBytes, UploadTurnBudget.BytesPerTurn(true, 1000.0 / 24.0, false));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Weak_UnknownPeriod_AssumesTheReference120Hz(double periodMs)
    {
        Assert.Equal(MiB, UploadTurnBudget.BytesPerTurn(true, periodMs, false));
        Assert.Equal(MiB / 2, UploadTurnBudget.BytesPerTurn(true, periodMs, true));
    }
}
