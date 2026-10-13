using System;
using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class GpuTeardownFenceTests
{
    private const int Removed = unchecked((int)0x887A0005);
    private const int Unexpected = unchecked((int)0x80004005);

    private struct Fence : IGpuTeardownFence
    {
        public bool RemovedNow, RemoveOnWait, FenceRemovedOnWait;
        public ulong Completed, SignalValue;
        public int SignalHr, ArmHr, Signals, Arms, Waits, RemovalReason;
        public uint WaitResult;
        public long Time;
        public ulong AdvanceEachWait;
        public int CompleteOnWait;
        public ulong DelayedCompletion;
        public bool IsRemoved() => RemovedNow;
        public ulong CompletedValue() => Completed;
        public void RecordRemoval(int reason) { RemovedNow = true; RemovalReason = reason; }
        public int Signal(ulong value) { Signals++; SignalValue = value; return SignalHr; }
        public int Arm(ulong value) { Arms++; return ArmHr; }
        public uint Wait(int milliseconds)
        {
            Waits++; Time += milliseconds;
            Completed += AdvanceEachWait;
            if (CompleteOnWait != 0 && Waits == CompleteOnWait) Completed = DelayedCompletion;
            if (FenceRemovedOnWait) Completed = ulong.MaxValue;
            if (RemoveOnWait) RecordRemoval(Removed);
            return WaitResult;
        }
    }

    [Fact]
    public void HealthyDevice_SignalsAndWaitsForRetirement()
    {
        var fence = new Fence { Completed = 3, AdvanceEachWait = 1 };
        Assert.True(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(1, fence.Signals); Assert.Equal(5UL, fence.SignalValue);
        Assert.Equal(1, fence.Arms); Assert.Equal(2, fence.Waits);
        Assert.False(fence.RemovedNow);
    }

    [Fact]
    public void HealthyTarget_OnlyWaitsForItsHorizon_WithoutSignallingTheSharedQueue()
    {
        var fence = new Fence { Completed = 10, AdvanceEachWait = 1 };
        Assert.True(GpuTeardownFence.Drain(ref fence, 12, signal: false));
        Assert.Equal(0, fence.Signals); Assert.Equal(1, fence.Arms); Assert.Equal(2, fence.Waits);
    }

    [Fact]
    public void AlreadyRemoved_SkipsEveryNormalDrainOperation()
    {
        var fence = new Fence { RemovedNow = true };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(0, fence.Signals); Assert.Equal(0, fence.Arms); Assert.Equal(0, fence.Waits);
    }

    [Fact]
    public void FenceRemovalSentinel_SkipsSignalAndRecordsLoss()
    {
        var fence = new Fence { Completed = ulong.MaxValue };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(Removed, fence.RemovalReason);
        Assert.Equal(0, fence.Signals); Assert.Equal(0, fence.Waits);
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005))]
    [InlineData(unchecked((int)0x887A0006))]
    [InlineData(unchecked((int)0x887A0007))]
    [InlineData(unchecked((int)0x887A0020))]
    public void RemovalDuringSignal_IsRecordedWithoutArmingOrWaiting(int hr)
    {
        var fence = new Fence { SignalHr = hr };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(hr, fence.RemovalReason);
        Assert.Equal(0, fence.Arms); Assert.Equal(0, fence.Waits);
    }

    [Fact]
    public void RemovalDuringEventRegistration_ReturnsWithoutWaiting()
    {
        var fence = new Fence { ArmHr = Removed };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(Removed, fence.RemovalReason); Assert.Equal(0, fence.Waits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovalDuringWait_AllowsDeviceAndTargetRelease(bool signal)
    {
        var fence = new Fence { RemoveOnWait = true };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal));
        Assert.Equal(1, fence.Waits); Assert.Equal(Removed, fence.RemovalReason);
    }

    [Fact]
    public void FenceRemovalSentinelDuringWait_CannotMasqueradeAsHealthyRetirement()
    {
        var fence = new Fence { FenceRemovedOnWait = true };
        Assert.False(GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Equal(1, fence.Waits); Assert.Equal(Removed, fence.RemovalReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnexpectedNativeError_PreservesTheOperationAndHresult(bool duringSignal)
    {
        var fence = new Fence { SignalHr = duringSignal ? Unexpected : 0, ArmHr = duringSignal ? 0 : Unexpected };
        var error = Assert.Throws<InvalidOperationException>(() => GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.Contains(duringSignal ? "queue.Signal" : "SetEventOnCompletion", error.Message);
        Assert.Contains("0x80004005", error.Message);
        Assert.False(fence.RemovedNow); Assert.Equal(0, fence.Waits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HealthyFence_StillRequiresRetirementAfterManyPollsWithoutProgress(bool signal)
    {
        var fence = new Fence { CompleteOnWait = 50, DelayedCompletion = 5 };
        Assert.True(GpuTeardownFence.Drain(ref fence, 5, signal));
        Assert.Equal(50, fence.Waits); Assert.Equal(5000, fence.Time);
        Assert.Equal(signal ? 1 : 0, fence.Signals);
        Assert.Equal(5UL, fence.Completed); Assert.False(fence.RemovedNow);
    }

    [Fact]
    public void HealthyFenceProgress_RetainsTheDrainAcrossManyPolls()
    {
        var fence = new Fence { AdvanceEachWait = 1 };
        Assert.True(GpuTeardownFence.Drain(ref fence, 30, signal: true));
        Assert.Equal(3000, fence.Time); Assert.False(fence.RemovedNow);
    }

    [Fact]
    public void FailedNativeWait_IsNotMaskedAsSuccessfulRetirement()
    {
        var fence = new Fence { WaitResult = uint.MaxValue, AdvanceEachWait = 5 };
        Assert.Throws<InvalidOperationException>(() => GpuTeardownFence.Drain(ref fence, 5, signal: true));
        Assert.False(fence.RemovedNow);
    }

    [Fact]
    public void NeverInitializedDevice_CanBeDisposedTwice()
    {
        var device = new D3D12Device(new StringTable());
        device.Dispose();
        device.Dispose();
    }
}
