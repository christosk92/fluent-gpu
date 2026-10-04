using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// F117: closing a pop-out (or a windowed flyout) waits only for the GPU work its OWN swapchain still has in flight, the rule
/// <c>Resize</c> already follows, never a device-wide <c>WaitForGpu</c> that also drained the main window's queued frames while
/// the UI sat parked inside the close. The value waited on is <see cref="D3D12Device.TargetFenceHorizon"/>; it must come from the
/// target's own fence ledger alone. Device-free: bare swapchain objects stamped by hand, no GPU.
/// </summary>
public sealed unsafe class TargetTeardownFenceTests
{
    private static D3D12Device NewDevice() => new(new StringTable());

    private static D3D12Swapchain NewTarget(D3D12Device device, byte ordinal)
        => new(device, HWND.NULL, 1, 1, composited: false, desktopAcrylic: false, default(ColorF), 0f, ordinal);

    [Fact]
    public void ASecondaryTargetsHorizon_NeverReadsThePrimarysInFlightFrame()
    {
        var device = NewDevice();
        var primary = NewTarget(device, 1);
        var child = NewTarget(device, 2);

        // The main window has a frame far ahead in the queue; the pop-out last submitted much earlier.
        primary.Frame.FenceValues[0] = 900; primary.Frame.FenceValues[1] = 905; primary.Frame.LastSubmitFence = 905;
        child.Frame.FenceValues[0] = 10; child.Frame.FenceValues[1] = 12; child.Frame.LastSubmitFence = 12;

        Assert.Equal(12UL, D3D12Device.TargetFenceHorizon(child.Frame));    // waiting for 12 retires the child's work and nothing of the primary's
        Assert.Equal(905UL, D3D12Device.TargetFenceHorizon(primary.Frame));
    }

    [Fact]
    public void TheHorizon_FoldsInThePerBackBufferLedger_SoAStaleMaxCannotUnderWait()
    {
        var device = NewDevice();
        var target = NewTarget(device, 2);
        target.Frame.LastSubmitFence = 5;
        target.Frame.FenceValues[2] = 40;   // a stamp the running max missed

        Assert.Equal(40UL, D3D12Device.TargetFenceHorizon(target.Frame));
    }

    [Fact]
    public void ATargetThatNeverSubmitted_HasNothingToWaitFor()
    {
        var device = NewDevice();
        var target = NewTarget(device, 2);

        Assert.Equal(0UL, D3D12Device.TargetFenceHorizon(target.Frame));   // WaitForFenceValue(0) returns at once
    }

    [Fact]
    public void DisposingASecondaryTarget_WithNoDevice_ReleasesItWithoutAnyWait()
    {
        var device = NewDevice();
        var child = NewTarget(device, 2);
        child.Frame.FenceValues[0] = 12; child.Frame.LastSubmitFence = 12;   // stamped, but there is no queue to wait on

        device.DisposeSwapchain(child);

        Assert.True(child.Disposed);
        device.DisposeSwapchain(child);   // idempotent
    }
}
