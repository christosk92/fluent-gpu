using System;
using System.Threading;
using System.Diagnostics;
using FluentGpu.Pal;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Single-consumer scene-adopt → record → submit → present loop, independent of UI reactive/layout work.
/// Active compositor motion uses its own optional display-clock subscription with a refresh-derived fallback;
/// clean idle blocks without releasing the retained scene. Resize/recovery/shutdown take priority over render ticks.
/// <see cref="DrainSync"/> is an explicit request/ack rendezvous for deterministic hosts, not the production async path.
/// </summary>
public sealed class RenderThread : IDisposable
{
    /// <summary>The recursive recorder needs the same stack reserve as the UI recording path.</summary>
    public const int RecordingStackBytes = 32 * 1024 * 1024;
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
    // Detached-window routing: after draining the parent host's OWN seam each turn, drain any registered CHILD host seams
    // (pop-out video windows) on THIS render thread, so a second AppHost's swapchain presents through the ONE render thread
    // that owns the shared device's submit/present (never a second render thread → no undetected _cmdList/_queue/_fence race).
    // Null on a host with no children (or a child host, which has no render thread of its own). Runs regardless of whether
    // the parent published this turn — a child wake carries no parent publish, so the parent-seam TryAcquire may no-op.
    private readonly Action? _extraDrain;
    private readonly Func<bool>? _needsTick;
    private readonly Action? _tick;
    private readonly Func<long>? _tickPeriod;
    private long _nextTick;
    private long _requestedDrains, _completedDrains;
    private readonly IRenderDisplayClock? _displayClock;
    private readonly WaitHandle[]? _displayWaits;
    private volatile bool _running = true;
    private ulong _presentAck;
    private static readonly bool s_trace = FluentGpu.Foundation.Diag.EnvFlag("FG_DL_TRACE");   // device-lost recovery trace

    public RenderThread(SceneFramePublisher publisher, Action<RenderFrame> submitPresent, bool async = false,
                        DeviceLostCoordinator? deviceLost = null, Action? recover = null, Action? windowWake = null,
                        Action? extraDrain = null, Func<bool>? needsTick = null, Action? tick = null,
                        Func<long>? tickPeriod = null, IRenderDisplayClock? displayClock = null)
    {
        _publisher = publisher;
        _submitPresent = submitPresent;
        _deviceLost = deviceLost;
        _recover = recover;
        _windowWake = windowWake;
        _extraDrain = extraDrain;
        _needsTick = needsTick;
        _tick = tick;
        _tickPeriod = tickPeriod;
        _displayClock = displayClock;
        if (displayClock is not null) _displayWaits = [_wake, displayClock.Tick];
        _thread = new Thread(Loop, RecordingStackBytes) { Name = "fgpu-render", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The publish-seq of the last frame this thread presented (acquire read) — the "how far behind is render"
    /// diagnostic the UI reads; never a pacer.</summary>
    public ulong PresentAck => Volatile.Read(ref _presentAck);

    private void Loop()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);   // this thread is the SOLE ComPtr owner for submit/present
        while (true)
        {
            bool motionDue = _needsTick?.Invoke() == true;
            _displayClock?.SetActive(motionDue);
            int waitMs = motionDue ? Math.Max(0, (int)Math.Ceiling(
                (_nextTick - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency)) : Timeout.Infinite;
            if (motionDue && _displayClock?.IsAvailable == true)
            {
                // A clock capability loss signals its tick handle once. The bounded timeout is a
                // backstop; ordinary compositor turns are driven by the independent display event.
                int backstop = Math.Clamp((int)Math.Ceiling(
                    (_tickPeriod?.Invoke() ?? Stopwatch.Frequency / 60) * 2000.0 / Stopwatch.Frequency), 8, 100);
                WaitHandle.WaitAny(_displayWaits!, backstop);
            }
            else _wake.WaitOne(waitMs);
            long turnStart = Stopwatch.GetTimestamp();
            long requestedDrain = Volatile.Read(ref _requestedDrains);
            if (!_running) break;
            // Step 4: device-lost recovery takes priority. The UI observed a lost device and is BLOCKING (not publishing)
            // until RecoverDone. Rebuild the device here (render-confined — this thread is the sole ComPtr owner), mark
            // done, and nudge the UI out of its clean block. No resize/present can be pending (the UI blocks both).
            if (_deviceLost is { } dl && dl.RecoverRequest != 0 && dl.RecoverDone == 0)
            {
                if (s_trace) Console.Error.WriteLine("[dl] render: recover gate — invoking RecoverDevice");
                try { _recover?.Invoke(); }
                catch (Exception ex) { Console.Error.WriteLine($"[dl] render: RecoverDevice THREW: {ex}"); }
                dl.RecoverDone = 1;   // set even on failure so the UI unblocks (it re-detects if still lost) — never hang
                _windowWake?.Invoke();
                if (s_trace) Console.Error.WriteLine("[dl] render: RecoverDone set + UI nudged");
                continue;
            }
            // Step 2: a resize is pending. Park HERE (before any TryAcquire/submit/present ComPtr touch), tell the UI the
            // loop is idle, and block until the UI finishes the fenced swapchain Resize + calls Resume. Then re-loop and
            // wait for the next real wake (the post-resize full-relayout republish).
            if (Volatile.Read(ref _resizeQuiesce) != 0)
            {
                _displayClock?.SetActive(false);
                // Consume this request before acknowledging it. The UI may Resume then immediately
                // request another park; clearing after the resume wait would erase that newer request.
                Volatile.Write(ref _resizeQuiesce, 0);
                _resizeIdle.Set();
                _resumeResize.WaitOne();
                continue;
            }
            // Acquire the LATEST published frame (DropOldest coalesce — intermediate publishes since the last wake are
            // dropped, §11). One AutoResetEvent wake ⇒ one latest-frame present; the arena the UI is now writing is a
            // DIFFERENT ring slot than the published one this reads, so there is no torn read.
            if (_publisher.TryAcquire(out var rf))   // consumer side (bound Render here)
            {
                _submitPresent(rf);
                // The publish seq this thread last presented (release write) — the UI reads it as a diagnostic
                // (how far behind render is); production pacing is on the compositor tick, not on this ack.
                Volatile.Write(ref _presentAck, rf.PublishSeq);
            }
            else if (_needsTick?.Invoke() == true) _tick?.Invoke();
            // Detached child hosts: present any freshly-published child frame on ITS own swapchain, on this same render
            // thread. Runs every turn (a child's wake may carry no parent publish, so it must not hang off the parent
            // TryAcquire above). Cheap no-op when no child has published since its last present (dedup in TryAcquire).
            _extraDrain?.Invoke();
            _nextTick = turnStart + Math.Max(1, _tickPeriod?.Invoke() ?? Stopwatch.Frequency / 60);
            if (requestedDrain > Volatile.Read(ref _completedDrains))
            {
                Volatile.Write(ref _completedDrains, requestedDrain);
                _done.Set();
            }
        }
        _displayClock?.SetActive(false);
    }

    /// <summary>UI thread, FORCE-SYNC (Step 4): wake the render thread and block until it has submitted+presented the
    /// just-published frame.</summary>
    public void DrainSync()
    {
        ThreadGuard.AssertUi();
        if (_disposed) return;   // teardown race (window closed / thread joined): nothing to present, don't touch a disposed event
        long requested = Interlocked.Increment(ref _requestedDrains);
        _wake.Set();
        while (Volatile.Read(ref _completedDrains) < requested) _done.WaitOne();
    }

    /// <summary>UI thread, ASYNC (Step 5): wake the render thread and RETURN immediately — the UI proceeds while the
    /// render thread submits/presents on its own timeline (the smoothness win: the GPU fence-wait stall no longer bounds
    /// back to the UI thread). EXPERIMENTAL / default-off — safe shipping additionally requires the UploadImage
    /// producer→consumer handoff + the resize/device-lost rendezvous + a green GPU soak (landing plan §9).</summary>
    public void WakeAsync()
    {
        ThreadGuard.AssertUi();
        if (_disposed) return;   // teardown race (e.g. a detached child's last publish after the parent thread joined): drop it
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
        if (_disposed) return;   // teardown race: the loop is gone, nothing to park
        Volatile.Write(ref _resizeQuiesce, 1);
        _wake.Set();               // nudge the loop so it reaches the quiesce gate even if idle-parked on _wake
        _resizeIdle.WaitOne();     // acquire barrier: the loop is now parked on _resumeResize
    }

    /// <summary>UI thread, ASYNC (Step 2): release the render loop after the swapchain Resize completed.</summary>
    public void Resume()
    {
        ThreadGuard.AssertUi();
        if (_disposed) return;   // teardown race: paired with a Quiesce that also no-op'd
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
        // A timeout is not proof of termination: disposing events or GPU state while a blocked
        // submit is still running causes use-after-dispose. Ownership returns only after the join.
        _thread.Join();
        _displayClock?.Dispose();
        _wake.Dispose();
        _done.Dispose();
        _resizeIdle.Dispose();
        _resumeResize.Dispose();
    }
}
