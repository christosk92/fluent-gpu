using System;
using System.Threading;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The device-loss / follow-default state machine (spec §7.9) — <c>{Building, Running, Reinitializing, Retrying, Faulted}</c>
/// driven by an <see cref="IDeviceWatcher"/> (Windows <c>IMMNotificationClient</c>, cold) and by the render path's
/// sink-failure reports. A default-render-device change / unplug transitions <c>Running → Reinitializing</c>, rebuilds ONLY
/// the sink under a LIVE graph via <see cref="PcmAudioSession.RebuildSink"/> (sources, queue, <c>PreparedSlot</c>, and
/// position SURVIVE), re-measures latency, applies a short fade-in, and returns to <c>Running</c> — all OFF the RT thread,
/// on a dedicated COLD device thread. The RT feed thread and the signal/queue model never touch this path.
/// <para><b>Recovery (Wavee #112).</b> The timing lives in the pure, unit-tested <see cref="AudioDeviceRecoveryPolicy"/>:
/// watcher events are trailing-debounced (250 ms, capped at 1 s from the first event of a burst); a render-path
/// <see cref="ReportSinkFailure"/> may start a rebuild but can never postpone one (the 0.2.8 livelock: a dead sink
/// re-stamped the debounce every ~80 ms forever). An attempt whose endpoint is not <see cref="IAudioEndpoint.IsReady"/>
/// keeps the OLD sink playing and enters <c>Retrying</c> on the 250 ms / 1 s / 3 s ladder; exhaustion is <c>Faulted</c>,
/// which the next device event re-arms. The RT feed is parked around every attempt and restarted in a <c>finally</c>
/// whether or not the rebuild succeeded, so a failed attempt never leaves the feed stopped.</para>
/// <para>Individually drivable for deterministic tests (<see cref="OnDefaultDeviceChanged"/>/<see cref="TryRunDueRetry"/>/
/// <see cref="Fault"/>); on-box the watcher event is marshaled onto the cold thread.</para>
/// </summary>
public sealed class AudioDeviceController : IDisposable
{
    private readonly PcmAudioSession _session;
    private readonly IDeviceWatcher? _watcher;
    private readonly Func<IAudioEndpoint> _endpointFactory;
    private readonly AudioFeedThread? _feed;
    private readonly Signal<AudioDeviceState> _state = new(AudioDeviceState.Building);
    private readonly Action _watcherHandler;

    // The cold device thread: a single-consumer request pump (never the RT thread, never the control thread).
    // _gate serializes the policy + retry clock between the watcher thread, the RT reporter and the cold thread — an
    // uncontended monitor on a FAILURE path only (RecordSinkFailure fires after 8 dead blocks), outside the DSP tripwire.
    private readonly object _gate = new();
    private readonly AudioDeviceRecoveryPolicy _policy = new();
    private long _nextRetryAt = long.MinValue;   // TickCount64 of the scheduled ladder retry; MinValue = none
    private Thread? _coldThread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _run;
    private bool _disposed;
    private int _wakeDisposed;

    /// <summary>Create a controller over <paramref name="session"/>. <paramref name="endpointFactory"/> opens a fresh
    /// default endpoint on a rebuild; <paramref name="watcher"/> (optional) fires the follow-default event;
    /// <paramref name="feed"/> (optional) is parked around the swap on-box.</summary>
    public AudioDeviceController(PcmAudioSession session, Func<IAudioEndpoint> endpointFactory,
        IDeviceWatcher? watcher = null, AudioFeedThread? feed = null)
    {
        _session = session;
        _endpointFactory = endpointFactory;
        _watcher = watcher;
        _feed = feed;
        _watcherHandler = RequestRebuild;
        if (_watcher is not null) _watcher.DefaultDeviceChanged += _watcherHandler;
    }

    /// <summary>The current device state (spec §7.9).</summary>
    public IReadSignal<AudioDeviceState> State => _state;

    /// <summary>Transition <c>Building → Running</c> once the initial endpoint is live (call after the first open).</summary>
    public void MarkRunning()
    {
        if (_state.Peek() is AudioDeviceState.Building or AudioDeviceState.Reinitializing)
            _state.Value = AudioDeviceState.Running;
    }

    /// <summary>Start the cold device thread that services rebuild requests and ladder retries (on-box). Idempotent.</summary>
    public void Start()
    {
        if (_run || _disposed) return;
        _run = true;
        _coldThread = new Thread(ColdLoop) { IsBackground = true, Name = "FluentGpu.AudioDevice" };
        _coldThread.Start();
    }

    /// <summary>Marshal a follow-default rebuild onto the cold device thread (the watcher event handler), trailing-debounced
    /// 250 ms (Fix 4): the cold loop waits out a quiet window after the LATEST event, so two notifications from one physical
    /// device switch fold into a single rebuild — bounded at 1 s from the first event so a flapping device cannot defer it
    /// forever. A device event also cancels any scheduled ladder retry and resets the ladder (new information). If the cold
    /// thread is not running (deterministic tests), the caller drives <see cref="OnDefaultDeviceChanged"/> directly (no
    /// debounce — that entry point always rebuilds immediately, by design, so a test can assert one call = one rebuild).</summary>
    public void RequestRebuild()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _policy.NoteDeviceEvent(Environment.TickCount64);
            _nextRetryAt = long.MinValue;
        }
        Wake();
    }

    /// <summary>The render path's report that the live sink has stopped accepting frames (<see cref="PcmAudioSession"/>
    /// after a sustained run of dead writes, or a typed <see cref="AudioDeviceLostException"/>). Alloc-free. Starts a
    /// rebuild request if none is pending; NEVER re-stamps a pending one (a dead sink reports every ~80 ms — re-stamping was
    /// the 0.2.8 livelock), and is ignored while a ladder retry is scheduled or the ladder is exhausted (<c>Faulted</c>
    /// waits for the next device event — otherwise a dead sink would drive an attempt every 250 ms forever). The one
    /// exception is a session that still wants sound whose kept sink is dead (<see cref="PcmAudioSession.OutputLive"/>
    /// false): a single request runs an attempt that finds the ladder spent, so the slow retry takes over at its 5 s rate.</summary>
    public void ReportSinkFailure()
    {
        if (_disposed) return;
        if (_state.Peek() == AudioDeviceState.Faulted && !(_session.WantsOutput && !_session.OutputLive)) return;
        bool started;
        lock (_gate)
        {
            started = _policy.NoteSinkFailure(Environment.TickCount64, retryScheduled: _nextRetryAt != long.MinValue);
            if (started) _sinkFailureRequests++;
        }
        if (started) Wake();
    }

    private int _sinkFailureRequests;

    /// <summary>Deterministic test hook: true while a rebuild request (watcher or sink-failure) is pending and not yet taken.</summary>
    internal bool HasPendingRebuild { get { lock (_gate) return _policy.HasPending; } }

    /// <summary>Deterministic test hook: how many sink-failure reports actually STARTED a request (re-stamps are not requests).</summary>
    internal int SinkFailureRequests { get { lock (_gate) return _sinkFailureRequests; } }

    /// <summary>Perform the follow-default rebuild synchronously (spec §7.9) — the cold-thread body, also the deterministic
    /// test entry point. Rebuilds ONLY the sink; sources/queue/<c>PreparedSlot</c>/position survive. An endpoint that is
    /// null or not <see cref="IAudioEndpoint.IsReady"/> is disposed and the OLD sink kept (<c>Retrying</c> on the ladder,
    /// <c>Faulted</c> once exhausted); so is a refused <see cref="PcmAudioSession.RebuildSink"/> or a throwing factory.
    /// Never throws. Callers that own an RT feed must have parked it (<see cref="Attempt"/> does).</summary>
    public void OnDefaultDeviceChanged()
    {
        if (_disposed) return;
        var prev = _state.Peek();
        if (prev is not (AudioDeviceState.Running or AudioDeviceState.Building or AudioDeviceState.Faulted or AudioDeviceState.Retrying)) return;

        _state.Value = AudioDeviceState.Reinitializing;
        try
        {
            var next = _endpointFactory();
            if (_disposed) { next?.Dispose(); return; }
            if (next is null || !next.IsReady)
            {
                // The new default device exists but is not Initialize-able yet (a jack switch settles over hundreds of
                // ms) or there is no device: keep the previous sink — it may still be audible — and retry on the ladder.
                next?.Dispose();
                ScheduleRetry();
                return;
            }
            if (!_session.RebuildSink(next))
            {
                next.Dispose();
                ScheduleRetry();
                return;
            }
            // Fix 3: re-derive the feed's ms→frames sizing against the (possibly new) live rate. Safe here specifically
            // because Attempt parks the feed (Stop()) around every rebuild before calling in — Resize plain-writes its
            // fields with no synchronization and is not safe while the RT/worker threads are live.
            _feed?.Resize(_session.Format);
            lock (_gate) _policy.ResetLadder();
            _state.Value = AudioDeviceState.Running;
        }
        catch (Exception)
        {
            // No endpoint could be opened (all devices gone, factory threw) — back off on the ladder; the next device
            // event re-arms it even after exhaustion.
            ScheduleRetry();
        }
    }

    /// <summary>The listener asked to play while no endpoint is live (<c>Faulted</c> or between ladder steps): treat it like a
    /// device event: reset the ladder and rebuild now. Pressing Play used to do nothing once the ladder was exhausted.</summary>
    public void Rearm()
    {
        if (_disposed) return;
        if (_state.Peek() is AudioDeviceState.Faulted or AudioDeviceState.Retrying) RequestRebuild();
    }

    /// <summary>Force the <c>Faulted</c> state (an unrecoverable device error). Idempotent. The next device event re-arms.</summary>
    public void Fault() { if (!_disposed) _state.Value = AudioDeviceState.Faulted; }

    /// <summary>Deterministic test hook: run the scheduled ladder retry if it is due at <paramref name="nowMs"/>
    /// (<see cref="Environment.TickCount64"/> domain). Returns false when no retry is scheduled or it is not yet due.</summary>
    internal bool TryRunDueRetry(long nowMs)
    {
        if (_disposed) return false;
        lock (_gate)
        {
            if (_nextRetryAt == long.MinValue || nowMs < _nextRetryAt) return false;
        }
        Attempt();
        return true;
    }

    // One rebuild attempt on the cold thread: take the pending request, park the RT feed, run the rebuild, and ALWAYS
    // restart the feed if it was parked — a failed rebuild must never leave the feed stopped (the old sink may still be
    // audible, and the RT loop is what reports a dead one).
    private void Attempt()
    {
        var feed = _feed;
        bool parked = false;
        try
        {
            lock (_gate)
            {
                _policy.ClearPending();
                _nextRetryAt = long.MinValue;
            }
            if (feed is not null)
            {
                feed.Stop();
                if (!feed.IsStopped)
                {
                    // A thread missed its join bound; the swap is unsafe now. The ladder retry Stop()s again — by then the
                    // late thread has exited (its run flag is already false) — instead of parking the feed forever.
                    ScheduleRetry();
                    return;
                }
                parked = true;
            }
            OnDefaultDeviceChanged();
        }
        catch (Exception) { ScheduleRetry(); }   // OnDefaultDeviceChanged never throws; this guards the parking itself
        finally
        {
            if (parked && feed is not null && _run && !_disposed && feed.IsStopped)
            {
                try { feed.Start(); } catch (Exception) { /* a torn-down feed (disposing) must not fault the cold thread */ }
            }
        }
    }

    // Schedule the next ladder step (→ Retrying) or declare exhaustion (→ Faulted; the next device event resets the ladder
    // and re-enters the machine). Wakes the cold loop so it recomputes its wait.
    // Past the ladder, a session that still wants to play keeps trying every SlowRetryMs while its kept sink is dead: a
    // Bluetooth endpoint can stay "Active" but refuse Initialize (AUDCLNT_E_DEVICE_INVALIDATED) for longer than the 4 s
    // ladder and then recover WITHOUT a default-device event, which left Wavee silent until restart. A paused or idle
    // session goes Faulted as before, and so does one whose kept sink still plays: every attempt parks the RT feed around a
    // synchronous open, and a refusing endpoint's open drained the audible sink's ~100 ms buffer every 5 s for as long as
    // the listener kept playing. Play (Rearm) or the next device event tries that device again.
    private const int SlowRetryMs = 5000;

    private void ScheduleRetry()
    {
        int? delay;
        lock (_gate)
        {
            delay = _policy.NextRetryDelayMs();
            if (delay is null && _session.WantsOutput && !_session.OutputLive) delay = SlowRetryMs;
            _nextRetryAt = delay is int d ? Environment.TickCount64 + d : long.MinValue;
        }
        _state.Value = delay is null ? AudioDeviceState.Faulted : AudioDeviceState.Retrying;
        if (delay is not null) Wake();
    }

    private void Wake()
    {
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    // Milliseconds until the next thing to do (pending request due, or ladder retry due); Timeout.Infinite when idle.
    private int WaitMsLocked(long nowMs)
    {
        int due = _policy.DueInMs(nowMs);
        if (_nextRetryAt != long.MinValue) due = Math.Min(due, (int)Math.Clamp(_nextRetryAt - nowMs, 0, int.MaxValue));
        return due == int.MaxValue ? Timeout.Infinite : due;
    }

    private void ColdLoop()
    {
        try
        {
            while (_run)
            {
                int wait;
                lock (_gate) wait = WaitMsLocked(Environment.TickCount64);
                if (wait != 0) _wake.WaitOne(wait);
                if (!_run) break;

                bool due;
                lock (_gate) due = WaitMsLocked(Environment.TickCount64) == 0;
                if (!due) continue;   // woken early (a new request re-stamped the window, or a spurious Set) — recompute
                Attempt();
            }
        }
        finally { if (_disposed) DisposeWake(); }
    }

    private void DisposeWake()
    {
        if (Interlocked.Exchange(ref _wakeDisposed, 1) == 0) _wake.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher is not null) _watcher.DefaultDeviceChanged -= _watcherHandler;
        _run = false;
        Wake();
        bool joined = _coldThread is null;
        try { joined = _coldThread?.Join(2000) ?? true; } catch { }
        if (joined)
        {
            _coldThread = null;
            DisposeWake();
        }
        // An uncooperative endpoint factory retains its wake handle until its cold worker has exited.
    }
}
