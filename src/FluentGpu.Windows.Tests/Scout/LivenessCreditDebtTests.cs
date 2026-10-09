using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// An unpaced turn's liveness take (bounded at max(2 x refresh, 34 ms), F208) proceeds with the credit held even when the slot
/// never opened, so the Present that spends it queues a frame no wait paid for. DXGI's waitable counts above the depth (its
/// maximum covers every depth SetMaximumFrameLatency may raise it to), so once both queued frames retire it is one count rich and,
/// unrepaid, every later wait opens a frame early. Device-free: a real OS semaphore with that headroom stands in for the waitable
/// (depth 1, the previous present still queued: count 0).
/// </summary>
public sealed unsafe class LivenessCreditDebtTests
{
    private const int UnpacedTake = -34;   // RenderThread's unpaced liveness take at 60 Hz

    private static (D3D12Device Device, D3D12Swapchain Sc, Semaphore Waitable) NewTarget()
    {
        var device = new D3D12Device(new StringTable());
        var waitable = new Semaphore(0, 16);
        var sc = new D3D12Swapchain(device, HWND.NULL, 1, 1, composited: false, desktopAcrylic: false, default(ColorF), 0f, 2);
        sc.FrameLatencyWaitable = (HANDLE)waitable.SafeWaitHandle.DangerousGetHandle();
        sc.HasLatencyWaitable = true;
        return (device, sc, waitable);
    }

    [Fact]
    public void ATimedOutLivenessTake_IsRepaid_SoTheSlotIsBusyAgainWhileAFrameIsQueued()
    {
        var (device, sc, waitable) = NewTarget();
        using var _ = waitable;

        Assert.True(device.TryTakeLatencyCredit(sc, UnpacedTake));   // A did not retire within the bound: proceeds, nothing taken
        Assert.True(sc.LatencyCreditHeld);
        sc.LatencyCreditHeld = false;                                // Present B ran on top of A
        waitable.Release(2);                                         // A and B retire: two counts for one wait

        Assert.True(device.TryTakeLatencyCredit(sc, 5));             // the next turn gets the slot
        sc.LatencyCreditHeld = false;                                // Present C ran; C is still queued (no release)

        Assert.False(device.TryTakeLatencyCredit(sc, 5));            // depth 1 with C queued: the slot must be busy
        Assert.False(sc.LatencyCreditHeld);
    }

    [Fact]
    public void AnOverstatedDebt_NeverTakesTheSlotItself()
    {
        var (device, sc, waitable) = NewTarget();
        using var _ = waitable;

        Assert.True(device.TryTakeLatencyCredit(sc, UnpacedTake));   // times out: one count owed
        sc.LatencyCreditHeld = false;                                // Present B ran...
        waitable.Release();                                          // ...but only one count ever comes back: nothing is queued

        for (int turn = 0; turn < 3; turn++)
        {
            Assert.True(device.TryTakeLatencyCredit(sc, 5));         // the open slot is this turn's, never "repaid" from under it
            sc.LatencyCreditHeld = false;                            // its Present ran and retired
            waitable.Release();
        }
    }
}
