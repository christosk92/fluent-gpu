using System.Collections.Generic;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

// ── Popup swapchains kept between opens, and released without waiting (AppHost's popup pool) ──────────────────────────
//
// A popup window's swapchain (the desktop-acrylic material every leased menu window has) used to be built for each open and
// released at each close: 1.7-2.7 ms of the shared render thread per open (8.7-10.1 ms for the first in a process, most of it
// the composition backdrop), and a release that waited on the popup's own fence on that thread. The host now parks a closed
// popup's window and swapchain together and hands both to the next popup:
//  - ParkPopup keeps the swapchain, its RTV heap, its command list and its composition backdrop, the buffers at the size the
//    popup had: no ResizeBuffers at park (a shrink to 1x1 used to run after the owner's present and took 3 ms, and 26 / 144 ms
//    when it met the owner's own swap chain resizing into full screen), and none at all when the next popup has the same size.
//    A parked popup holds its three back buffers (the largest Wavee menu, the 298x465 DIP track menu window: 1.66 MB at scale
//    1, 3.74 MB at 1.5), a three-descriptor RTV heap, one closed command list and the composition visuals; the pool keeps at
//    most three. A popup created but never built (the host's idle prewarm) is built at 1x1.
//  - UnparkPopup resets the open/close motion and re-arms the reveal latch: the host's reveal waits for the NEW popup's first
//    present, and the direct route clears every frame it draws, so nothing of the previous menu can be composed.
//  - A popup released for good (DisposeSwapchain) drops its composition backdrop at once - its window may be destroyed right
//    after - and releases its D3D12 resources once its fence retired, never blocking the render thread on it.
//  - A device loss releases every parked popup for good (RecoverDeviceCore): UnparkPopup then answers false and the host
//    disposes it and builds a new one.
public sealed unsafe partial class D3D12Device
{
    // Render thread. Set when a parked popup still holds its open-size buffers or a released one still holds its D3D12 resources;
    // serviced after the primary's present and on elided turns (ServicePopupUpkeep).
    private bool _popupUpkeepPending;
    private int _primaryPresentThreadId;   // the thread that presents the primary (the render thread under a render loop)
    // Popups released for good whose frames had not retired yet: out of _swapchains (nothing records or presents them), their
    // backdrop gone, their buffers / RTV heap / command list released by ServicePopupUpkeep once the fence passed them.
    private readonly List<D3D12Swapchain> _retiringPopupTargets = new(2);

    internal bool ParkPopup(D3D12Swapchain sc)
    {
        AssertDeviceOwner();
        if (!sc.DesktopAcrylic || sc.Disposed || sc.RetireDeferred || sc.BuildFailed || ReferenceEquals(sc, _primarySwapchain)) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int awaitingBuild = sc.PopupFramesAwaitingBuild, deferred = sc.PopupFramesDeferred;
        ResetPopupFrameState(sc);
        string build = "built";
        if (sc.BuildPending)
        {
            // Never built (the idle prewarm, or a popup closed before its first frame): built at the parked size where every popup
            // build runs, after the owner's next present (BuildPendingPopups) or on a turn that presents nothing
            // (ServicePopupUpkeep), never inside the owner's frame. The prewarm pays the first-in-process backdrop there, at idle.
            sc.W = 1; sc.H = 1;
            _popupBuildPending = true;
            _popupUpkeepPending = true;
            build = "pending";
        }
        sc.Parked = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Diag.Line($"[d3d12.present] popup parked target={sc.Ordinal} build={build} size={sc.W}x{sc.H} heldBytes={ParkedPopupBytes(sc)} framesAwaitingBuild={awaitingBuild} framesDeferredBusySlot={deferred} ms={ms:F2}");
        return true;
    }

    internal bool UnparkPopup(D3D12Swapchain sc)
    {
        AssertDeviceOwner();
        if (sc.Disposed || sc.RetireDeferred || !sc.Parked || sc.BuildFailed) return false;
        sc.Parked = false;   // the new placement's Resize sizes it: a no-op at the size it was parked at
        ResetPopupFrameState(sc);
        try { sc.Backdrop?.ResetForReuse(); }
        catch (System.Exception ex)
        {
            Diag.Line($"[d3d12.present] popup reuse failed target={sc.Ordinal}: {ex.Message}");
            return false;
        }
        sc.PpEpoch++;
        sc.ArmRevealExemption();
        return true;
    }

    private static void ResetPopupFrameState(D3D12Swapchain sc)
    {
        sc.SubmitDeferred = false;
        sc.PresentNoWaitOnce = false;
        sc.PopupDeferSinceQpc = 0;
        sc.PopupDeferTurns = 0;
        System.Threading.Volatile.Write(ref sc.PopupFrameOwed, 0);
        sc.BuildWaitTurns = 0;
        sc.PopupFramesAwaitingBuild = 0;
        sc.PopupFramesDeferred = 0;
        sc.ChromePending = false;
    }

    /// <summary>A popup swapchain released for good: its composition backdrop goes now (so the host may destroy the window right
    /// after), its D3D12 resources once its own frames retired. Never waits on the fence. Render thread.</summary>
    private void RetirePopupTarget(D3D12Swapchain target)
    {
        Diag.Line($"[d3d12.present] popup released target={target.Ordinal} built={(target.SwapChain != null ? 1 : 0)} parked={(target.Parked ? 1 : 0)} framesAwaitingBuild={target.PopupFramesAwaitingBuild} framesDeferredBusySlot={target.PopupFramesDeferred}");
        target.Parked = false;
        target.Backdrop?.Dispose();
        target.Backdrop = null;
        _swapchains.Remove(target);
        ulong done = _fence == null ? ulong.MaxValue : global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (done == ulong.MaxValue || TargetFenceHorizon(target.Frame) <= done)
        {
            // Retired (or the device is gone and its fence never will): nothing of this target is in flight.
            ReleaseSwapchainResources(target);
            Diag.Line($"[d3d12.teardown] phase=target-released target={target.Ordinal} recovery={_recoveryCount} deferred=0");
            return;
        }
        target.RetireDeferred = true;
        _retiringPopupTargets.Add(target);
        _popupUpkeepPending = true;
    }

    /// <summary>The prewarm's never-built popup is built (on a turn with no owner present), and a popup released for good lets go
    /// of its D3D12 resources once its frames retired. Never waits. Render thread; after the primary's present (Present) and on
    /// turns that submit nothing (ReclaimCompletedUploads).</summary>
    private void ServicePopupUpkeep()
    {
        if (!_popupUpkeepPending || _fence == null) return;
        _popupUpkeepPending = false;
        ulong done = global::FluentGpu.Interop.Generated.ID3D12FenceVtbl.GetCompletedValue(_fence);
        if (done == ulong.MaxValue) { _popupUpkeepPending = true; return; }   // lost: RecoverDeviceCore releases them all
        for (int i = 0; i < _swapchains.Count; i++)
        {
            var sc = _swapchains[i];
            if (sc.Parked && sc.BuildPending) BuildPopupNow(sc);   // the prewarm's build on a turn with no owner present (logs its own line)
        }
        for (int i = _retiringPopupTargets.Count - 1; i >= 0; i--)
        {
            var t = _retiringPopupTargets[i];
            if (TargetFenceHorizon(t.Frame) > done) { _popupUpkeepPending = true; continue; }
            _retiringPopupTargets.RemoveAt(i);
            t.RetireDeferred = false;
            ReleaseSwapchainResources(t);
            Diag.Line($"[d3d12.teardown] phase=target-released target={t.Ordinal} recovery={_recoveryCount} deferred=1");
        }
    }

    /// <summary>Device loss / teardown: every popup still waiting for its fence is released now (the fence is dead or drained), and
    /// every parked popup is released for good (the host builds a new one on its next open). Render thread (or the parked owner).</summary>
    private void ReleasePopupPoolForRecovery()
    {
        for (int i = 0; i < _retiringPopupTargets.Count; i++)
        {
            _retiringPopupTargets[i].RetireDeferred = false;
            ReleaseSwapchainResources(_retiringPopupTargets[i]);
        }
        _retiringPopupTargets.Clear();
        for (int i = _swapchains.Count - 1; i >= 0; i--)
        {
            var sc = _swapchains[i];
            if (!sc.Parked) continue;
            ReleaseSwapchainResources(sc);   // Disposed = true: UnparkPopup answers false, a later Dispose is a no-op
            sc.Parked = false;
            _swapchains.RemoveAt(i);
        }
        _popupUpkeepPending = false;
    }

    /// <summary>What a parked popup's back buffers commit on this device (the driver's own allocation size for one buffer at its
    /// size, times the buffer count) plus its RTV heap's descriptors; the command list and the composition visuals are driver / DWM
    /// objects with no size to query. For the park log line.</summary>
    private ulong ParkedPopupBytes(D3D12Swapchain sc)
    {
        if (_device == null || sc.BackBuffers[0] == null) return 0;
        D3D12_RESOURCE_DESC desc = sc.BackBuffers[0]->GetDesc();
        D3D12_RESOURCE_ALLOCATION_INFO info = _device->GetResourceAllocationInfo(0, 1, &desc);
        return info.SizeInBytes * FRAME_COUNT + (ulong)FRAME_COUNT * _rtvSize;
    }
}
