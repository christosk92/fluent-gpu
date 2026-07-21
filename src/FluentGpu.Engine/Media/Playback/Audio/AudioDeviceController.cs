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
    private bool _disposed;

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

    /// <summary>Marshal a follow-default rebuild onto the cold device thread (the watcher event handler). If the cold thread
    /// is not running (deterministic tests), the caller drives <see cref="OnDefaultDeviceChanged"/> directly.</summary>
    public void RequestRebuild()
    {
        Interlocked.Exchange(ref _pending, 1);
        _wake.Set();
    }

    /// <summary>Perform the follow-default rebuild synchronously (spec §7.9) — the cold-thread body, also the deterministic
    /// test entry point. Rebuilds ONLY the sink; sources/queue/<c>PreparedSlot</c>/position survive. Never throws.</summary>
    public void OnDefaultDeviceChanged()
    {
        if (_disposed) return;
        var prev = _state.Peek();
        if (prev is not (AudioDeviceState.Running or AudioDeviceState.Building)) return;

        _state.Value = AudioDeviceState.Reinitializing;
        try
        {
            var next = _endpointFactory() ?? throw new InvalidOperationException("No audio endpoint available.");
            bool ok = _session.RebuildSink(next);
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
        while (_run)
        {
            _wake.WaitOne();
            if (!_run) break;
            if (Interlocked.Exchange(ref _pending, 0) == 0) continue;

            // Park the RT feed around the swap so no callback reads a half-swapped endpoint (on-box).
            bool wasRunning = _feed is not null;
            if (wasRunning) _feed!.Stop();
            OnDefaultDeviceChanged();
            if (wasRunning && _state.Peek() == AudioDeviceState.Running) _feed!.Start();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher is not null) _watcher.DefaultDeviceChanged -= _watcherHandler;
        _run = false;
        _wake.Set();
        try { _coldThread?.Join(500); } catch { }
        _coldThread = null;
        _wake.Dispose();
    }
}
