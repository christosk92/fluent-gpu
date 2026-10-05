using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The frame-latency waitable is a SEMAPHORE: one wait takes one count (a credit) and only a Present that actually runs
/// gives it back. A submit whose Present stood down (cloaked / hidden / OCCLUDED-latched HWND) therefore keeps its credit,
/// and the next submit on that swapchain must skip the wait instead of blocking the whole 1000 ms bound on the shared render
/// thread. The bookkeeping used to exist for the primary swapchain only; it is per swapchain now. Device-free: the
/// swapchains are bare <see cref="D3D12Swapchain"/> objects whose latency waitable is a real OS semaphore standing in for
/// DXGI's (initial count 1, maximum 1 - the depth-1 contract), so the real take/hold/skip code runs with no GPU.
/// </summary>
public sealed unsafe class PresentCreditTests
{
    private sealed class Target : IDisposable
    {
        public readonly D3D12Swapchain Swapchain;
        public readonly Semaphore Waitable;

        public Target(D3D12Device device, byte ordinal, int initialCount = 1)
        {
            Waitable = new Semaphore(initialCount, 1);
            Swapchain = new D3D12Swapchain(device, HWND.NULL, 1, 1, composited: false, desktopAcrylic: false, default(ColorF), 0f, ordinal);
            Swapchain.FrameLatencyWaitable = (HANDLE)Waitable.SafeWaitHandle.DangerousGetHandle();
            Swapchain.HasLatencyWaitable = true;
        }

        /// <summary>What a Present that actually ran does: clear the credit, and the retiring frame re-signals the semaphore.</summary>
        public void PresentRan()
        {
            Swapchain.LatencyCreditHeld = false;
            Waitable.Release();
        }

        public void Dispose() => Waitable.Dispose();
    }

    private static D3D12Device NewDevice() => new(new StringTable());

    [Fact]
    public void ANonPrimaryWait_HoldsItsCredit_AndTheNextSubmitDoesNotBlock()
    {
        var device = NewDevice();
        using var child = new Target(device, ordinal: 2);

        device.WaitForLatency(child.Swapchain);              // the submit's wait: takes the only count
        Assert.True(child.Swapchain.LatencyCreditHeld);
        Assert.False(child.Waitable.WaitOne(0));             // the semaphore is at 0: a second real wait would block 1000 ms

        // The Present stood down (cloaked pop-out): nothing re-signaled. The next submit must see the held credit.
        var sw = Stopwatch.StartNew();
        Assert.True(device.TryTakeLatencyCredit(child.Swapchain, -1));
        Assert.True(sw.ElapsedMilliseconds < 250, $"a held credit must skip the wait; took {sw.ElapsedMilliseconds} ms");
        Assert.True(child.Swapchain.LatencyCreditHeld);
    }

    [Fact]
    public void APresentThatRuns_SpendsTheCredit_SoTheNextWaitTakesTheResignaledCount()
    {
        var device = NewDevice();
        using var child = new Target(device, ordinal: 2);

        device.WaitForLatency(child.Swapchain);
        child.PresentRan();                                  // credit spent, count back to 1
        Assert.False(child.Swapchain.LatencyCreditHeld);

        var sw = Stopwatch.StartNew();
        device.WaitForLatency(child.Swapchain);              // takes the re-signaled count immediately
        Assert.True(sw.ElapsedMilliseconds < 250);
        Assert.True(child.Swapchain.LatencyCreditHeld);
        Assert.False(child.Waitable.WaitOne(0));
    }

    [Fact]
    public void TwoSwapchains_KeepIndependentCredits()
    {
        var device = NewDevice();
        using var a = new Target(device, ordinal: 1);
        using var b = new Target(device, ordinal: 2);

        device.WaitForLatency(b.Swapchain);                  // b stands down and keeps its credit
        Assert.True(b.Swapchain.LatencyCreditHeld);
        Assert.False(a.Swapchain.LatencyCreditHeld);

        var sw = Stopwatch.StartNew();
        device.WaitForLatency(a.Swapchain);                  // a's own semaphore is untouched by b's stand-down
        Assert.True(sw.ElapsedMilliseconds < 250);
        Assert.True(a.Swapchain.LatencyCreditHeld);
    }

    [Fact]
    public void ABoundedTake_ThatDoesNotGetTheSlot_ReservesNothing()
    {
        var device = NewDevice();
        using var child = new Target(device, ordinal: 2, initialCount: 0);   // the previous present is still in flight

        Assert.False(device.TryTakeLatencyCredit(child.Swapchain, 5));
        Assert.False(child.Swapchain.LatencyCreditHeld);

        child.Waitable.Release();                                            // the present retires
        Assert.True(device.TryTakeLatencyCredit(child.Swapchain, 5));
        Assert.True(child.Swapchain.LatencyCreditHeld);
    }

    // F207: the render loop's park-request event rides every slot wait as {waitable, abort}, the waitable at index 0.
    private static D3D12Device NewDeviceWithAbort(out ManualResetEvent abort)
    {
        var device = NewDevice();
        abort = new ManualResetEvent(false);
        device.SetSubmitAbortHandle(abort.SafeWaitHandle.DangerousGetHandle());
        return device;
    }

    [Fact]
    public void AnAbortedTake_TakesNothing_AndLeavesTheCreditUnheld_InEveryForm()
    {
        var device = NewDeviceWithAbort(out var abort);
        using var abortEvent = abort;
        using var child = new Target(device, ordinal: 2, initialCount: 0);   // the slot never opens

        abort.Set();                                                         // the UI asked to park
        var sw = Stopwatch.StartNew();
        Assert.False(device.TryTakeLatencyCredit(child.Swapchain, -1));      // the 1 s liveness form: ended at once, not after 1000 ms
        Assert.True(sw.ElapsedMilliseconds < 250, $"an abort must end the liveness wait; took {sw.ElapsedMilliseconds} ms");
        Assert.False(child.Swapchain.LatencyCreditHeld);
        Assert.False(device.TryTakeLatencyCredit(child.Swapchain, 50));      // the bounded form: nothing taken either
        Assert.False(child.Swapchain.LatencyCreditHeld);

        device.WaitForLatency(child.Swapchain);                              // the submit's own wait: skipped, and it must not claim a credit
        Assert.False(child.Swapchain.LatencyCreditHeld);
    }

    [Fact]
    public void AnOpenSlot_StillWinsOverAnAbort_AndIsTheOnlyOutcomeThatHoldsTheCredit()
    {
        var device = NewDeviceWithAbort(out var abort);
        using var abortEvent = abort;
        using var child = new Target(device, ordinal: 2, initialCount: 1);   // the slot is open

        abort.Set();                                                         // both are signaled: the waitable is index 0
        Assert.True(device.TryTakeLatencyCredit(child.Swapchain, -1));
        Assert.True(child.Swapchain.LatencyCreditHeld);
        Assert.False(child.Waitable.WaitOne(0));                             // the count was really taken

        child.PresentRan();                                                  // the credit is spent and the retiring frame re-signals
        Assert.True(device.TryTakeLatencyCredit(child.Swapchain, 5));        // bounded form, same ordering
        Assert.True(child.Swapchain.LatencyCreditHeld);
    }

    [Fact]
    public void AnUnsignaledAbortHandle_ChangesNothing()
    {
        var device = NewDeviceWithAbort(out var abort);
        using var abortEvent = abort;
        using var child = new Target(device, ordinal: 2, initialCount: 0);

        Assert.False(device.TryTakeLatencyCredit(child.Swapchain, 5));       // times out as before, nothing reserved
        Assert.False(child.Swapchain.LatencyCreditHeld);
        child.Waitable.Release();
        Assert.True(device.TryTakeLatencyCredit(child.Swapchain, 5));
        Assert.True(child.Swapchain.LatencyCreditHeld);

        device.SetSubmitAbortHandle(0);                                      // a cleared handle is the plain wait
        child.PresentRan();
        device.WaitForLatency(child.Swapchain);
        Assert.True(child.Swapchain.LatencyCreditHeld);
    }

    [Fact]
    public void ASwapchainWithoutAWaitable_NeverHoldsACredit()
    {
        var device = NewDevice();
        var sc = new D3D12Swapchain(device, HWND.NULL, 1, 1, composited: false, desktopAcrylic: false, default(ColorF), 0f, 3);

        device.WaitForLatency(sc);
        Assert.False(sc.LatencyCreditHeld);
        Assert.True(device.TryTakeLatencyCredit(sc, -1));
        Assert.False(sc.LatencyCreditHeld);
    }
}
