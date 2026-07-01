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
    private readonly bool _async;                          // Step 5: false = force-sync (UI blocks in DrainSync); true = async (WakeAsync, UI proceeds)
    private volatile bool _running = true;
    private ulong _presentAck;

    public RenderThread(SceneFramePublisher publisher, Action<RenderFrame> submitPresent, bool async = false)
    {
        _publisher = publisher;
        _submitPresent = submitPresent;
        _async = async;
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

    public void Dispose()
    {
        _running = false;
        _wake.Set();               // unblock the loop so it can observe !_running and exit
        _thread.Join(1000);
        _wake.Dispose();
        _done.Dispose();
    }
}
