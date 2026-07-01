using System;
using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The dedicated render thread (design/subsystems/threading-render-seam.md §1; landing plan §5 Step 4), Cut A.
///
/// Runs the consume→submit→present loop OFF the UI thread. <b>Step 4 is FORCE-SYNC:</b> after the UI publishes a frame
/// it wakes this thread and BLOCKS in <see cref="DrainSync"/> until the frame is presented — so there is no actual
/// overlap yet (zero perf win), but submit + present + the blocking GPU fence-waits now EXECUTE here rather than on the
/// UI thread. That proves the seam + the ComPtr-on-render path deterministically before <b>Step 5</b> removes the UI
/// wait (the async flip that lands the smoothness win) — and Step 5 flips ONLY behind a green <c>seam.race</c> soak.
///
/// This type is OPT-IN (AppHost constructs it only when the render-thread gate is set); the engine ships single-thread
/// (the proven inline pass-through) by default until the soak is green. Single-consumer: exactly one render thread.
/// </summary>
public sealed class RenderThread : IDisposable
{
    private readonly Thread _thread;
    private readonly SceneFramePublisher _publisher;
    private readonly Action<RenderFrame> _submitPresent;   // runs ON this thread: (suppress vsync?) → SubmitDrawList(arena) → Present
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _done = new(false);
    // Step 2 (async resize rendezvous): a non-destructive park/resume handshake. The UI parks this loop (mutual exclusion)
    // before it mutates the swapchain/back-buffers/fence in Resize, then resumes it. _resizeIdle = loop → UI "I am parked,
    // no ComPtr touch in flight"; _resumeResize = UI → loop "Resize done, proceed". The AutoResetEvent Set/WaitOne pair is
    // a full memory barrier, publishing the UI's advanced fence/back-buffer/frame-index writes to the loop before it un-parks.
    private readonly AutoResetEvent _resizeIdle = new(false);
    private readonly AutoResetEvent _resumeResize = new(false);
    private int _resizeQuiesce;
    // Step 4 (async device-lost recovery): the UI observes a lost device, sets RecoverRequest + wakes this loop; the loop
    // rebuilds the device here (render-confined) and signals RecoverDone + nudges the UI. Null ⇒ no recovery wired.
    private readonly DeviceLostCoordinator? _deviceLost;
    private readonly Action? _recover;      // runs ON this thread: _device.RecoverDevice() under AssertRender
    private readonly Action? _windowWake;   // thread-safe UI wake (PostMessage WM_NULL) to nudge the UI out of its clean block
    private readonly bool _async;                          // Step 5: false = force-sync (UI blocks in DrainSync); true = async (WakeAsync, UI proceeds)
    private volatile bool _running = true;
    private ulong _presentAck;

    public RenderThread(SceneFramePublisher publisher, Action<RenderFrame> submitPresent, bool async = false,
                        DeviceLostCoordinator? deviceLost = null, Action? recover = null, Action? windowWake = null)
    {
        _publisher = publisher;
        _submitPresent = submitPresent;
        _async = async;
        _deviceLost = deviceLost;
        _recover = recover;
        _windowWake = windowWake;
        _thread = new Thread(Loop) { Name = "fgpu-render", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The publish-seq of the last frame this thread presented (acquire read) — the present-ack the UI reads to
    /// pace passive effects; also the future async-mode "how far behind is render" signal.</summary>
    public ulong PresentAck => Volatile.Read(ref _presentAck);

    private void Loop()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);   // this thread is the SOLE ComPtr owner for submit/present
        while (true)
        {
            _wake.WaitOne();
            if (!_running) break;
            // Step 4: device-lost recovery takes priority. The UI observed a lost device and is BLOCKING (not publishing)
            // until RecoverDone. Rebuild the device here (render-confined — this thread is the sole ComPtr owner), mark
            // done, and nudge the UI out of its clean block. No resize/present can be pending (the UI blocks both).
            if (_deviceLost is { } dl && dl.RecoverRequest != 0 && dl.RecoverDone == 0)
            {
                _recover?.Invoke();
                dl.RecoverDone = 1;
                _windowWake?.Invoke();
                continue;
            }
            // Step 2: a resize is pending. Park HERE (before any TryAcquire/submit/present ComPtr touch), tell the UI the
            // loop is idle, and block until the UI finishes the fenced swapchain Resize + calls Resume. Then re-loop and
            // wait for the next real wake (the post-resize full-relayout republish).
            if (Volatile.Read(ref _resizeQuiesce) != 0)
            {
                _resizeIdle.Set();
                _resumeResize.WaitOne();
                Volatile.Write(ref _resizeQuiesce, 0);
                continue;
            }
            // Acquire the LATEST published frame (DropOldest coalesce — intermediate publishes since the last wake are
            // dropped, §11). One AutoResetEvent wake ⇒ one latest-frame present; the arena the UI is now writing is a
            // DIFFERENT ring slot than the published one this reads, so there is no torn read.
            if (_publisher.TryAcquire(out var rf))   // consumer side (bound Render here)
            {
                _submitPresent(rf);
                Volatile.Write(ref _presentAck, rf.PublishSeq);
            }
            if (!_async) _done.Set();   // force-sync only: unblock the UI's DrainSync
        }
    }

    /// <summary>UI thread, FORCE-SYNC (Step 4): wake the render thread and block until it has submitted+presented the
    /// just-published frame.</summary>
    public void DrainSync()
    {
        ThreadGuard.AssertUi();
        _wake.Set();
        _done.WaitOne();
    }

    /// <summary>UI thread, ASYNC (Step 5): wake the render thread and RETURN immediately — the UI proceeds while the
    /// render thread submits/presents on its own timeline (the smoothness win: the GPU fence-wait stall no longer bounds
    /// back to the UI thread). EXPERIMENTAL / default-off — safe shipping additionally requires the UploadImage
    /// producer→consumer handoff + the resize/device-lost rendezvous + a green GPU soak (landing plan §9).</summary>
    public void WakeAsync()
    {
        ThreadGuard.AssertUi();
        _wake.Set();
    }

    /// <summary>UI thread, ASYNC (Step 2): PARK the render loop before mutating the swapchain in Resize, and BLOCK until
    /// it confirms it is idle (no submit/present in flight) — mutual exclusion so the UI's fenced <c>ResizeBuffers</c> +
    /// back-buffer release can't race a concurrent present. Pair with <see cref="Resume"/> in a try/finally. The final
    /// pre-park frame (if the loop was mid-submit) completes at the OLD size before the park; the stale published frame is
    /// dropped (DropOldest) and the post-resize relayout republishes at the new size.</summary>
    public void Quiesce()
    {
        ThreadGuard.AssertUi();
        Volatile.Write(ref _resizeQuiesce, 1);
        _wake.Set();               // nudge the loop so it reaches the quiesce gate even if idle-parked on _wake
        _resizeIdle.WaitOne();     // acquire barrier: the loop is now parked on _resumeResize
    }

    /// <summary>UI thread, ASYNC (Step 2): release the render loop after the swapchain Resize completed.</summary>
    public void Resume()
    {
        ThreadGuard.AssertUi();
        _resumeResize.Set();
    }

    private bool _disposed;

    /// <summary>Stop + join the render thread. Idempotent (a pre-capture quiesce may call it before AppHost.Dispose).
    /// After this returns the render thread is gone, so the caller (UI) is the sole GPU-ComPtr owner again.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _wake.Set();               // unblock the loop so it can observe !_running and exit
        _resumeResize.Set();       // Step 2: also release a loop parked mid-quiesce, so teardown can't hit the Join timeout
        _thread.Join(1000);
        _wake.Dispose();
        _done.Dispose();
        _resizeIdle.Dispose();
        _resumeResize.Dispose();
    }
}
