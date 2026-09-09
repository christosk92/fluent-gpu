using System;
using System.Threading;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The device-loss / follow-default state machine (spec §7.9) — <c>{Building, Running, Reinitializing, Faulted}</c> driven
/// by an <see cref="IDeviceWatcher"/> (Windows <c>IMMNotificationClient</c>, cold). A default-render-device change / unplug
/// transitions <c>Running → Reinitializing</c>, rebuilds ONLY the sink under a LIVE graph via
/// <see cref="PcmAudioSession.RebuildSink"/> (sources, queue, <c>PreparedSlot</c>, and position SURVIVE), re-measures
/// latency, applies a short fade-in, and returns to <c>Running</c> — all OFF the RT thread, on a dedicated COLD device
/// thread. A fatal fault (no fallback endpoint) transitions to <c>Faulted</c>. The RT feed thread and the signal/queue
/// model never touch this path.
/// <para>Individually drivable for deterministic tests (<see cref="OnDefaultDeviceChanged"/>/<see cref="Fault"/>);
/// on-box the watcher event is marshaled onto the cold thread.</para>
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
    private readonly object _gate = new();
    private Thread? _coldThread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _run;
    private int _pending;      // coalesced default-change requests
    private long _pendingSince;   // Environment.TickCount64 of the MOST RECENT request — the debounce clock (Fix 4)
    private bool _disposed;
    private int _wakeDisposed;

    // Debounce window (spec §7.9 Fix 4 / root cause A4): the OS raises the default-device-changed notification before
    // the new endpoint's mix format is settled, so ONE physical device switch routinely fires two notifications a few
    // hundred ms apart (observed on the dev box: 48000 then 44100 within ~800 ms) — each would otherwise drive its own
    // full rebuild. 250 ms comfortably covers the observed gap without making a genuine follow-default switch feel slow.
    private const int DebounceMs = 250;

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

    /// <summary>Start the cold device thread that services follow-default rebuild requests (on-box). Idempotent.</summary>
    public void Start()
    {
        if (_run || _disposed) return;
        _run = true;
        _coldThread = new Thread(ColdLoop) { IsBackground = true, Name = "FluentGpu.AudioDevice" };
        _coldThread.Start();
    }

    /// <summary>Marshal a follow-default rebuild onto the cold device thread (the watcher event handler), debounced 250 ms
    /// (Fix 4): the cold loop waits out a quiet window after the LATEST request, so two notifications from one physical
    /// device switch fold into a single rebuild instead of two. If the cold thread is not running (deterministic tests),
    /// the caller drives <see cref="OnDefaultDeviceChanged"/> directly (no debounce — that entry point always rebuilds
    /// immediately, by design, so a test can assert one call = one rebuild).</summary>
    public void RequestRebuild()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pendingSince, Environment.TickCount64);
        Interlocked.Exchange(ref _pending, 1);
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Perform the follow-default rebuild synchronously (spec §7.9) — the cold-thread body, also the deterministic
    /// test entry point. Rebuilds ONLY the sink; sources/queue/<c>PreparedSlot</c>/position survive. Never throws.</summary>
    public void OnDefaultDeviceChanged()
    {
        if (_disposed) return;
        var prev = _state.Peek();
        if (prev is not (AudioDeviceState.Running or AudioDeviceState.Building or AudioDeviceState.Faulted)) return;

        _state.Value = AudioDeviceState.Reinitializing;
        try
        {
            var next = _endpointFactory() ?? throw new InvalidOperationException("No audio endpoint available.");
            if (_disposed) { next.Dispose(); return; }
            bool ok = _session.RebuildSink(next);
            if (!ok) next.Dispose();
            // Fix 3: re-derive the feed's ms→frames sizing against the (possibly new) live rate. Safe here specifically
            // because the cold loop parks the feed (Stop()) around every rebuild before calling in — Resize plain-writes
            // its fields with no synchronization and is not safe while the RT/worker threads are live.
            if (ok) _feed?.Resize(_session.Format);
            _state.Value = ok ? AudioDeviceState.Running : AudioDeviceState.Faulted;
        }
        catch (Exception)
        {
            // No fallback endpoint (all devices gone) — terminal until a device returns (a later change re-enters here).
            _state.Value = AudioDeviceState.Faulted;
        }
    }

    /// <summary>Force the terminal <c>Faulted</c> state (an unrecoverable device error). Idempotent.</summary>
    public void Fault() { if (!_disposed) _state.Value = AudioDeviceState.Faulted; }

    private void ColdLoop()
    {
        try
        {
            while (_run)
            {
                _wake.WaitOne();
                if (!_run) break;
                if (Interlocked.Exchange(ref _pending, 0) == 0) continue;

                // Debounce (Fix 4): wait out a quiet window after the LATEST request before rebuilding. A request landing
                // inside the window re-stamps `_pendingSince` (RequestRebuild, off this thread) and pushes the wait back out
                // instead of queuing a second rebuild — see RequestRebuild's doc for why this matters (the 48000-then-44100
                // double notification).
                long since;
                while (_run && (since = Environment.TickCount64 - Interlocked.Read(ref _pendingSince)) < DebounceMs)
                    _wake.WaitOne((int)(DebounceMs - since));
                if (!_run) break;

                // Park the RT feed around the swap so no callback reads a half-swapped endpoint (on-box).
                bool wasRunning = _feed is not null;
                if (wasRunning)
                {
                    _feed!.Stop();
                    if (!_feed.IsStopped) { _state.Value = AudioDeviceState.Faulted; continue; }
                }
                OnDefaultDeviceChanged();
                if (_run && !_disposed && wasRunning && _state.Peek() == AudioDeviceState.Running) _feed!.Start();
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
        try { _wake.Set(); } catch (ObjectDisposedException) { }
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
