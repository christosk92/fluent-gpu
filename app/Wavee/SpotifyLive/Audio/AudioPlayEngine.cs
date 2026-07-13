using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio.Host.Dsp;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Host-side play engine: fetch → AES-CTR decrypt → decode (Vorbis via vendored NVorbis, FLAC via FlacBox) →
/// WASAPI. Owns ONE WASAPI renderer + EQ/limiter/session-COM and drives one or two <see cref="DecodePipeline"/>s from a
/// single output thread. A crossfade decodes both sources and equal-power mixes them PER SAMPLE in the output loop
/// (sample-accurate, pause-aware), so there is no second WASAPI stream and no cross-thread COM churn. The fade envelope
/// is baked at Write time in the queued (<see cref="WasapiRenderer.ReleasedFrames"/>) domain — a block written now is
/// heard ~one buffer later, and ReleasedFrames stalls while paused, making the fade inherently pause-aware.</summary>
internal sealed class AudioPlayEngine : IDisposable
{
    const int RendererBufferMs = 800;
    const int StartPrebufferMs = 420;
    const int WriteStallWarnMs = 650;
    const int EndSeekGuardMs = 250;

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly WaveeLogger _log;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;
    readonly AudioBodyDiskCache? _bodyDisk;
    readonly WasapiRenderer _renderer = new();
    readonly EqualizerProcessor _equalizer = new();
    readonly Limiter _limiter = new();
    readonly object _gate = new();
    readonly Timer _tick;

    // ── output-device routing + Windows session volume (Phase A/B) ────────────────────────────────────────────────────
    static readonly Guid WaveeVolumeContext = Guid.NewGuid();   // per-process; our own session sets carry it so the sink filters echoes
    static readonly Guid IID_IAudioSessionEvents = new("24918ACC-64B3-37C1-8CA9-74A66E9957A8");
    static readonly StrategyBasedComWrappers SessionComWrappers = new();
    readonly IAudioDeviceMonitor _monitor;
    readonly OutputDeviceRouter _router;
    readonly AudioSessionEventsSink _sessionSink;
    readonly IntPtr _sessionSinkPtr;
    double _volumeSlider = 1.0;

    sealed record PendingReroute(string? DeviceId, bool PauseFirst);
    PendingReroute? _pendingReroute;   // consumed via Interlocked.Exchange at output-loop top

    // ── the two decode pipelines + the single output thread ───────────────────────────────────────────────────────────
    DecodePipeline? _current;
    DecodePipeline? _incoming;
    Thread? _outputThread;
    CancellationTokenSource? _outputCts;

    long _renderBaseFrames;        // ReleasedFrames value at which the CURRENT track's frame 0 sits (re-based on promotion)
    long _overlapReleasedBase;     // ReleasedFrames captured when an active fade began (frame 0 of the fade)
    long _overlapFadeFrames;       // fadeMs·rate/1000 — the fade length in queued frames
    bool _overlapActive;           // a per-sample crossfade is in flight

    // The pipeline being faded IN. Held here (not in _incoming) once an overlap starts so the _incoming slot is free for
    // the controller to prepare the FOLLOWING track during the fade (it schedules that as soon as it sees Started).
    DecodePipeline? _overlapIncoming;
    string _overlapToken = "";
    string _overlapTrackUri = "";
    int _overlapFadeMs;

    // Incoming metadata (mirrors the old prepared-slot fields; the engine now originates the Transition events).
    string _incomingToken = "";
    string _incomingTrackUri = "";
    long _incomingDurationMs;
    int _incomingFadeMs;
    volatile bool _incomingBodySupplied;

    string _fileIdHex = "";        // active file (for logging / MMCSS naming)
    long _loadStartTicks;
    long _pendingSeekMs = -1;      // engine-level: applies to whatever pipeline is current when consumed
    volatile bool _playing;
    volatile bool _prebuffering;
    volatile bool _buffering;
    volatile bool _rendererPrimed;
    volatile bool _disposed;
    volatile PlaybackRecoveryKind _recoveryKind;
    volatile bool _networkRecoveryDrained;
    volatile bool _networkDataAvailable;
    long _networkRecoveryQueuedFrames;
    DecodePipeline? _recoveryPipeline;
    Timer? _recoveryMonitor;
    int _terminalFaultEmitted;

    public event Action<AudioHostSignal>? State;
    /// <summary>The active track ended and there was nothing to promote (→ host emits Ended).</summary>
    public event Action? TrackFinished;
    /// <summary>Crossfade/gapless hand-off notices (Started/Completed/Missed) — the engine originates these now.</summary>
    public event Action<AudioTransitionSignal>? Transition;
    /// <summary>Device loss / fallback / auto-return / output-failed notices (fires even while idle).</summary>
    public event Action<OutputDeviceNotice>? DeviceNotice;
    /// <summary>An EXTERNAL Windows session-volume change (slider01, muted) to reflect in the UI (Phase B).</summary>
    public event Action<double, bool>? SessionVolumeChanged;

    public AudioPlayEngine(WaveeLogger log, Func<string, byte[], CdnDecryptor?>? nativeDecryptorFactory = null,
        AudioBodyDiskCache? bodyDisk = null, HttpClient? http = null, bool ownsHttp = false,
        IAudioDeviceMonitor? monitor = null)
    {
        _http = http ?? HttpPools.Get(HttpPool.Cdn);
        _ownsHttp = ownsHttp;
        _log = log;
        _nativeDecryptorFactory = nativeDecryptorFactory ?? ((_, _) => null);
        _bodyDisk = bodyDisk;
        _tick = new Timer(_ => { if (_playing) RaiseState(); }, null, 1000, 1000);

        _monitor = monitor ?? new WasapiAudioDeviceMonitor(log);
        _router = new OutputDeviceRouter(_monitor, log, () => Environment.TickCount64);
        _router.RouteInvalidated += r =>
        {
            _log.Info($"audio.device reroute reason={r.Reason} target={r.TargetDeviceId ?? "(default)"} pauseFirst={r.PauseFirst}");
            Interlocked.Exchange(ref _pendingReroute, new PendingReroute(r.TargetDeviceId, r.PauseFirst));
        };
        _router.Notice += n => { try { DeviceNotice?.Invoke(n); } catch (Exception ex) { _log.Info("audio.device notice dispatch failed: " + ex.Message); } };

        // The per-session events sink (Phase B). Its CCW is registered on every renderer session (re-registered per Init).
        _sessionSink = new AudioSessionEventsSink(WaveeVolumeContext, OnExternalSessionVolume, OnSessionDisconnected);
        try
        {
            IntPtr unknown = SessionComWrappers.GetOrCreateComInterfaceForObject(_sessionSink, CreateComInterfaceFlags.None);
            try
            {
                Guid iid = IID_IAudioSessionEvents;
                int hr = Marshal.QueryInterface(unknown, in iid, out IntPtr sessionEvents);
                if (hr < 0 || sessionEvents == IntPtr.Zero)
                    Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004002));
                _sessionSinkPtr = sessionEvents;
            }
            finally { Marshal.Release(unknown); }
            _renderer.RegisterSessionEvents(_sessionSinkPtr);
        }
        catch (Exception ex) { _log.Info("audio.device session-events registration failed: " + ex.Message); }
    }

    void OnExternalSessionVolume(double slider01, bool muted)
    {
        _volumeSlider = slider01;   // keep the cache in sync so a later re-init re-applies the right value
        try { SessionVolumeChanged?.Invoke(slider01, muted); } catch (Exception ex) { _log.Info("audio.device external-volume dispatch failed: " + ex.Message); }
    }

    void OnSessionDisconnected() => _router.ReportDeviceInvalidated();   // belt-and-braces beside IMMNotificationClient

    /// <summary>Choose the output endpoint (null/empty = system default). Callable while idle — the next Init resolves via
    /// the router; while playing, the router posts a re-route to the output thread.</summary>
    public void SetOutputDevice(string? deviceId) => _router.SetDesired(string.IsNullOrEmpty(deviceId) ? null : deviceId);

    public void SetOutputMuted(bool muted) => _renderer.SetSessionMute(muted, in WaveeVolumeContext);

    void ApplyPostInitSession()
    {
        // Re-apply the cached slider through the session boundary (a new session per Init); events are re-registered by
        // the renderer. Session scalar = slider cubed (VolumeTaper.Amplitude) — acoustically identical to the old
        // per-sample multiply, now bijective for two-way sync.
        try { _renderer.SetSessionVolume(VolumeTaper.Amplitude((float)_volumeSlider), in WaveeVolumeContext); } catch { }
    }

    // ── active-track lifecycle ────────────────────────────────────────────────────────────────────────────────────────
    public void LoadFastStart(in AudioFastStart cmd)
    {
        ReportSupersededOverlap("active load superseded prepared next");
        StopOutput();
        var pipeline = CreatePipeline();
        pipeline.Load(cmd);
        _fileIdHex = cmd.FileIdHex;
        _loadStartTicks = Stopwatch.GetTimestamp();
        _rendererPrimed = false;
        Interlocked.Exchange(ref _terminalFaultEmitted, 0);
        ClearNetworkRecovery(raiseState: false);
        bool hasHead = cmd.HeadBytes.Length > 0;
        _prebuffering = hasHead;
        _buffering = !hasHead;
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => OutputThreadMain(cts.Token))
        {
            IsBackground = true,
            Name = "wavee-output",
            Priority = ThreadPriority.AboveNormal,
        };
        lock (_gate)
        {
            _current = pipeline;
            _incoming = null;
            _overlapActive = false;
            _renderBaseFrames = 0;
            _pendingSeekMs = -1;
            _outputCts = cts;
            _outputThread = thread;
        }
        thread.Start();
        RaiseState();
    }

    public void SupplyBody(in AudioStreamHandle cmd)
    {
        var current = _current;
        if (current is null) return;
        var handle = cmd;
        _ = current.SupplyBodyAsync(handle);
    }

    // ── prepared-next (incoming) lifecycle ────────────────────────────────────────────────────────────────────────────
    public void PrepareIncoming(in AudioPrepareRequest request, int fadeMs)
    {
        var pipeline = CreatePipeline();
        try { pipeline.Load(request.Start); }   // head load only; NO decoder build, NO renderer touch
        catch { pipeline.Dispose(); throw; }
        DecodePipeline? replaced;
        lock (_gate)
        {
            replaced = _incoming;
            _incoming = pipeline;
            _incomingBodySupplied = false;
            _incomingToken = request.Token;
            _incomingTrackUri = request.Start.TrackUri;
            _incomingDurationMs = request.Start.DurationMs;
            _incomingFadeMs = Math.Max(0, fadeMs);
        }
        replaced?.Dispose();
        _log.Info($"audio prepared-next token={request.Token} track={request.Start.TrackUri} overlap={request.AllowOverlap} fade={_incomingFadeMs}ms");
    }

    public void SupplyIncomingBody(string token, in AudioStreamHandle body)
    {
        DecodePipeline? pipeline;
        lock (_gate)
        {
            pipeline = _incoming;
            if (pipeline is null || !string.Equals(_incomingToken, token, StringComparison.Ordinal)) return;
            _incomingBodySupplied = true;
        }
        var handle = body;
        _ = pipeline.SupplyBodyAsync(handle);
    }

    public AudioPrepareCancelResult CancelIncoming(string token)
    {
        DecodePipeline? cancelled;
        lock (_gate)
        {
            if (_overlapActive && string.Equals(_overlapToken, token, StringComparison.Ordinal))
                return AudioPrepareCancelResult.AlreadyStarted;   // the hand-off is already committed
            if (_incoming is null || !string.Equals(_incomingToken, token, StringComparison.Ordinal))
                return AudioPrepareCancelResult.NotFound;
            cancelled = _incoming;
            _incoming = null;
            ClearIncomingMetadataLocked();
        }
        cancelled?.Dispose();
        return AudioPrepareCancelResult.Cancelled;
    }

    // ── transport ─────────────────────────────────────────────────────────────────────────────────────────────────────
    public void Play()
    {
        _playing = true;
        if (_rendererPrimed) _renderer.Start();
        else _log.Info($"play intent accepted for {_fileIdHex}: waiting for first PCM buffer before starting renderer");
        RaiseState();
    }

    public void Pause() { _playing = false; _renderer.Pause(); RaiseState(); }

    public void Stop()
    {
        _playing = false;
        ReportSupersededOverlap("stop");
        StopOutput();
        RaiseState();
    }

    // Mirror the old CancelSecondary rule: a manual load/stop reports Missed ONLY for an IN-FLIGHT fade (an already-
    // committed hand-off), never for a merely-prepared-not-started incoming (that one is dropped silently).
    void ReportSupersededOverlap(string reason)
    {
        string? token = null, uri = null; long pos = 0;
        lock (_gate)
        {
            if (_overlapActive && _overlapIncoming is not null) { token = _overlapToken; uri = _overlapTrackUri; pos = PositionMs; }
        }
        if (token is not null) EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Missed, token, uri!, pos, 0, reason));
    }

    public void Seek(long ms)
    {
        // Clamp into the current track. A target at/past the end would make the decoder bisect to EOF and fail the seek;
        // landing just shy of the end still finishes the track naturally.
        var current = _current;
        var dur = current?.DurationMs ?? 0;
        if (dur > 0 && ms > dur - EndSeekGuardMs) ms = dur - EndSeekGuardMs;
        Interlocked.Exchange(ref _pendingSeekMs, Math.Max(0, ms));
    }

    public void SetVolume(double v)
    {
        _volumeSlider = Math.Clamp(v, 0, 1);
        _renderer.SetSessionVolume(VolumeTaper.Amplitude((float)_volumeSlider), in WaveeVolumeContext);
    }

    public void SetEqualizer(EqualizerSettings settings) => _equalizer.Configure(settings);

    /// <summary>True while a Seek() target has been posted but not yet consumed by the output loop.</summary>
    public bool HasPendingSeek => Volatile.Read(ref _pendingSeekMs) >= 0;

    public long PositionMs
    {
        get
        {
            DecodePipeline? timeline;
            long baseFrames;
            lock (_gate)
            {
                // CrossfadeStarted is the semantic track boundary: the controller switches CurrentTrack (and its
                // duration) to the incoming item as soon as that signal is emitted. Keep the host clock on the same
                // item. Reporting the outgoing near-EOF timeline here made the new track's seek bar jump to 100% and
                // its elapsed/remaining labels read duration/-0:00 for the whole fade.
                bool incomingIsCurrent = _overlapActive && _overlapIncoming is not null;
                timeline = incomingIsCurrent ? _overlapIncoming : _current;
                baseFrames = incomingIsCurrent ? _overlapReleasedBase : _renderBaseFrames;
            }
            if (timeline is null) return 0;
            int rate = _renderer.SampleRate;
            long frames = _renderer.PlayedFrames - baseFrames;
            long ms = rate > 0 ? frames * 1000 / rate : 0;
            return Math.Max(0, timeline.SeekBaseMs + ms);
        }
    }

    long ReleasedPositionMs(DecodePipeline current)
    {
        int rate = _renderer.SampleRate;
        long frames = _renderer.ReleasedFrames - Interlocked.Read(ref _renderBaseFrames);
        long ms = rate > 0 ? frames * 1000 / rate : 0;
        return Math.Max(0, current.SeekBaseMs + ms);
    }

    // ── the single output thread ──────────────────────────────────────────────────────────────────────────────────────
    void OutputThreadMain(CancellationToken ct)
    {
        var current = _current;
        if (current is null) return;
        using var audioThread = AudioThreadPriority.TryEnter(_log, current.FileIdHex);
        try
        {
            // Wait until the current source can build its decoder (clear head present, or body attached / external open).
            while (!ct.IsCancellationRequested && !current.CanBuildDecoder)
            {
                if (current.Faulted)
                {
                    _log.Info($"decode setup aborted {current.FileIdHex}: source faulted before decoder build");
                    EmitTerminalFailure(current.Failure ?? new IOException("audio source faulted before decoder build"), current);
                    return;
                }
                Thread.Sleep(10);
            }
            if (ct.IsCancellationRequested) return;

            current.BuildDecoder();
            InitRenderer(current);
            _renderBaseFrames = 0;
            _log.Info($"decode {current.FileIdHex}: renderer initialized device={_renderer.OpenedDeviceId ?? "(default)"} fallback={_renderer.OpenedAsFallback} buffer={RendererBufferMs}ms startPrebuffer={StartPrebufferMs}ms");
            if (_playing) _log.Info($"decode {current.FileIdHex}: renderer start deferred until {StartPrebufferMs}ms PCM is queued");
            OutputLoop(ct);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { if (!ct.IsCancellationRequested) _log.Info($"decode setup interrupted {current.FileIdHex}: stream disposed"); }
        catch (AudioDeviceInvalidatedException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                _log.Info($"decode setup device-open failed {current.FileIdHex}: 0x{ex.Hr:X8}");
                _router.ReportOpenFailed(ex.Hr);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                DecodePipeline? failed;
                lock (_gate) failed = _overlapActive && _overlapIncoming is not null ? _overlapIncoming : _current;
                _log.Info($"audio.output.failed file={failed?.FileIdHex ?? current.FileIdHex}: {ex.GetType().Name}: {ex.Message}");
                EmitTerminalFailure(ex, failed ?? current);
            }
        }
    }

    void InitRenderer(DecodePipeline p)
    {
        string? initTarget = _router.ResolveTarget();
        _renderer.Init(p.SampleRate, p.Channels, RendererBufferMs, initTarget);
        _router.NotifyOpened(_renderer.OpenedDeviceId, _renderer.OpenedAsFallback);
        ApplyPostInitSession();
    }

    void OutputLoop(CancellationToken ct)
    {
        var bufCur = new float[16384];
        var bufInc = new float[16384];
        var bufMix = new float[16384];
        bool rendererStartedAfterBuffer = false;
        bool loggedFirstPcm = false;
        bool loggedBodyPcm = false;
        long preStartFrames = 0;
        long lastWriteTicks = 0;
        int lastGen0 = GC.CollectionCount(0);
        int lastGen1 = GC.CollectionCount(1);
        int lastGen2 = GC.CollectionCount(2);
        bool loggedSeekWaitingForBody = false;

        while (!ct.IsCancellationRequested)
        {
            var current = _current;
            if (current is null) break;

            // 1. Device re-route: poll retry deadlines, latch renderer faults, then re-open on THIS thread. If a fade is
            //    in flight, complete it forward first (§2.6) so we only ever reroute a single promoted stream.
            _router.Tick(Environment.TickCount64);
            if (_renderer.Faulted) _router.ReportDeviceInvalidated();
            var reroute = Interlocked.Exchange(ref _pendingReroute, null);
            if (reroute is not null)
            {
                if (_overlapActive) { PromoteOverlap(); current = _current; if (current is null) break; }
                ApplyReroute(reroute, current);
                rendererStartedAfterBuffer = false;
                preStartFrames = 0;
                lastWriteTicks = 0;
            }

            // 2. Seek consume — a seek cuts an in-flight fade FORWARD first, then seeks the promoted current.
            long seek = Interlocked.Exchange(ref _pendingSeekMs, -1);
            if (seek >= 0)
            {
                if (_overlapActive) { PromoteOverlap(); current = _current; if (current is null) break; }
                if (!current.SeekReady)
                {
                    Interlocked.CompareExchange(ref _pendingSeekMs, seek, -1);
                    if (!loggedSeekWaitingForBody)
                    {
                        _log.Info($"seek deferred target={seek}ms: waiting for CDN body length");
                        loggedSeekWaitingForBody = true;
                    }
                }
                else
                {
                    loggedSeekWaitingForBody = false;
                    var outcome = current.SeekTo(seek);
                    if (outcome == DecodePipeline.SeekOutcome.Applied)
                    {
                        _renderer.Reset();
                        _renderBaseFrames = 0;
                        _rendererPrimed = false;
                        rendererStartedAfterBuffer = false;
                        preStartFrames = 0;
                        lastWriteTicks = 0;
                        _log.Info($"head-check {current.FileIdHex}: seek applied target={seek}ms; renderer will restart after {StartPrebufferMs}ms PCM is queued");
                    }
                    else if (outcome == DecodePipeline.SeekOutcome.Failed)
                    {
                        RaiseState();
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _pendingSeekMs, seek, -1);
                    }
                }
            }

            // 3. Boundary detection — start a crossfade when the current track is within fadeMs of its end (queued domain).
            if (!_overlapActive) TryStartOverlap(current);

            int channels = current.Channels;
            int rate = current.SampleRate;

            // 4. Read current + (if overlapping) incoming, mix per-sample, EQ+limiter ONCE on the sum, write.
            long beforeOffset = current.CurrentOffset;
            bool bodyAttachedBeforeRead = current.IsBodyAttached;
            int got = current.Read(bufCur, 0, bufCur.Length);

            int block;
            float[] writeBuf;
            if (_overlapActive)
            {
                long startFrame = _renderer.ReleasedFrames - _overlapReleasedBase;
                var inc = _overlapIncoming;
                if (inc is null) { PromoteOverlap(); continue; }
                if (got > 0)
                {
                    block = got;
                    Array.Clear(bufInc, 0, block);
                    inc.Fill(bufInc, 0, block);
                    CrossfadeMixer.MixEqualPower(bufCur.AsSpan(0, block), bufInc.AsSpan(0, block), bufMix.AsSpan(0, block), startFrame, _overlapFadeFrames, channels);
                    writeBuf = bufMix;
                }
                else
                {
                    // Current reached its end mid/late fade — drive the tail of the fade from incoming alone (outgoing = 0).
                    int gotInc = inc.Read(bufInc, 0, bufInc.Length);
                    if (gotInc <= 0) { PromoteOverlap(); continue; }
                    block = gotInc;
                    channels = inc.Channels;
                    rate = inc.SampleRate;
                    Array.Clear(bufCur, 0, block);
                    CrossfadeMixer.MixEqualPower(bufCur.AsSpan(0, block), bufInc.AsSpan(0, block), bufMix.AsSpan(0, block), startFrame, _overlapFadeFrames, channels);
                    writeBuf = bufMix;
                }
            }
            else
            {
                if (got <= 0)
                {
                    if (_playing && !rendererStartedAfterBuffer && preStartFrames > 0)
                    {
                        long preStartMs = rate > 0 ? preStartFrames * 1000L / rate : 0;
                        _renderer.Start();
                        rendererStartedAfterBuffer = true;
                        _log.Info($"head-check {current.FileIdHex}: renderer started with short prebuffer={preStartMs}ms before EOF elapsed={current.ElapsedSinceLoadMs}ms");
                    }
                    if (HandleCurrentEof(current, ct))
                    {
                        // gapless promotion happened — reset per-track prebuffer state and continue with the new current
                        rendererStartedAfterBuffer = false;
                        preStartFrames = 0;
                        lastWriteTicks = 0;
                        continue;
                    }
                    break;
                }
                block = got;
                writeBuf = bufCur;
            }

            long afterOffset = current.CurrentOffset;
            _equalizer.Process(writeBuf.AsSpan(0, block), rate, channels);
            _limiter.Process(writeBuf.AsSpan(0, block));
            _prebuffering = false;
            if (_recoveryKind != PlaybackRecoveryKind.Network || !_networkRecoveryDrained)
                _buffering = false;

            var writeStartTicks = Stopwatch.GetTimestamp();
            try { _renderer.Write(writeBuf.AsSpan(0, block), ct); }
            catch (AudioDeviceInvalidatedException ex)
            {
                _log.Info($"audio.device write fault 0x{ex.Hr:X8} — routing re-init");
                _router.ReportDeviceInvalidated();
                continue;
            }
            var writeEndTicks = Stopwatch.GetTimestamp();
            _rendererPrimed = true;
            var wroteFrames = channels > 0 ? block / channels : block;
            if (_recoveryKind == PlaybackRecoveryKind.Network)
            {
                if (_networkRecoveryDrained)
                {
                    _networkRecoveryQueuedFrames += wroteFrames;
                    long recoveryQueuedMs = rate > 0 ? _networkRecoveryQueuedFrames * 1000L / rate : 0;
                    if (_networkDataAvailable && recoveryQueuedMs >= StartPrebufferMs)
                    {
                        if (_playing) _renderer.Start();
                        CompleteNetworkRecovery();
                    }
                }
                else if (_networkDataAvailable)
                {
                    CompleteNetworkRecovery();
                }
            }
            if (!rendererStartedAfterBuffer) preStartFrames += wroteFrames;

            if (lastWriteTicks != 0)
            {
                var writeGapMs = TicksToMs(writeEndTicks - lastWriteTicks);
                if (writeGapMs >= WriteStallWarnMs)
                {
                    var gen0 = GC.CollectionCount(0);
                    var gen1 = GC.CollectionCount(1);
                    var gen2 = GC.CollectionCount(2);
                    _log.Info($"audio starvation {current.FileIdHex}: writeGap={writeGapMs}ms source={current.DescribeReadSource(beforeOffset, afterOffset)} offset={beforeOffset} queuedFrames={_renderer.ReleasedFrames} overlap={_overlapActive} gen0+={gen0 - lastGen0} gen1+={gen1 - lastGen1} gen2+={gen2 - lastGen2}");
                    lastGen0 = gen0;
                    lastGen1 = gen1;
                    lastGen2 = gen2;
                }
            }
            lastWriteTicks = writeEndTicks;

            if (!loggedFirstPcm)
            {
                loggedFirstPcm = true;
                long queuedMs = rate > 0 && channels > 0 ? block / channels * 1000L / rate : 0;
                _log.Info($"head-check {current.FileIdHex}: first PCM queued from={current.DescribeReadSource(beforeOffset, afterOffset)} samples={block} approx={queuedMs}ms bodyAttachedBeforeRead={bodyAttachedBeforeRead} bodyAttachedNow={current.IsBodyAttached} elapsed={current.ElapsedSinceLoadMs}ms");
            }
            if (!loggedBodyPcm && beforeOffset >= current.ClearHeadLength && current.ClearHeadLength > 0)
            {
                loggedBodyPcm = true;
                _log.Info($"head-check {current.FileIdHex}: PCM reads are now using attached body/ranged CDN offset={beforeOffset} elapsed={current.ElapsedSinceLoadMs}ms");
            }

            if (_playing && !rendererStartedAfterBuffer)
            {
                long preStartMs = rate > 0 ? preStartFrames * 1000L / rate : 0;
                if (preStartMs >= StartPrebufferMs)
                {
                    _renderer.Start();
                    rendererStartedAfterBuffer = true;
                    _log.Info($"head-check {current.FileIdHex}: renderer started after queued PCM prebuffer={preStartMs}ms elapsed={current.ElapsedSinceLoadMs}ms");
                }
            }

            // 5. Fade completion — once fadeFrames have been queued since the overlap began, promote incoming → current.
            if (_overlapActive)
            {
                long done = _renderer.ReleasedFrames - _overlapReleasedBase;
                if (done >= _overlapFadeFrames)
                {
                    PromoteOverlap();
                    rendererStartedAfterBuffer = true;   // the fade already had the renderer running
                    preStartFrames = 0;
                }
            }

            RaiseState();
        }
    }

    // Boundary: begin a crossfade if an overlap-eligible incoming source is ready and the current track is within fadeMs
    // of its end (measured in the QUEUED/ReleasedFrames domain so the fade lines up with what will be heard). BuildDecoder
    // blocks on the head read — safe here (this is the output thread). Format mismatch ⇒ skip overlap; §2.4 EOF hand-off.
    void TryStartOverlap(DecodePipeline current)
    {
        var inc = _incoming;
        if (inc is null || _incomingFadeMs <= 0 || !_incomingBodySupplied) return;
        if (!inc.CanBuildDecoder) return;
        long dur = current.DurationMs;
        if (dur <= 0) return;
        if (HasPendingSeek) return;   // position is stale until the seek is applied
        int rate = _renderer.SampleRate;
        if (rate <= 0) return;
        long pos = ReleasedPositionMs(current);
        if (dur - pos > _incomingFadeMs) return;

        try
        {
            if (!inc.DecoderBuilt) inc.BuildDecoder();
        }
        catch (Exception ex)
        {
            _log.Info($"crossfade incoming decoder build failed token={_incomingToken}: {ex.Message}");
            return;
        }

        if (inc.SampleRate != current.SampleRate || inc.Channels != current.Channels)
        {
            // §2.3: different sample-rate/channel layout — do NOT overlap; run current to EOF and gapless-promote (with a
            // renderer re-init) at the boundary, identical to a normal non-crossfade track change. (Music→music matches.)
            return;
        }

        // Commit the overlap under the gate. Move the fading-in pipeline OUT of _incoming into _overlapIncoming so the
        // _incoming slot is free for the controller to prepare the FOLLOWING track during the fade, and so a concurrent
        // CancelIncoming for this token now returns AlreadyStarted (it can no longer dispose the pipeline mid-mix).
        string token, uri; int fade;
        lock (_gate)
        {
            if (!ReferenceEquals(_incoming, inc)) return;   // cancelled/replaced while we built the decoder
            _overlapReleasedBase = _renderer.ReleasedFrames;
            _overlapFadeFrames = Math.Max(1, (long)_incomingFadeMs * rate / 1000);
            _overlapIncoming = inc;
            _overlapToken = _incomingToken; _overlapTrackUri = _incomingTrackUri; _overlapFadeMs = _incomingFadeMs;
            _incoming = null;
            ClearIncomingMetadataLocked();
            _overlapActive = true;
            token = _overlapToken; uri = _overlapTrackUri; fade = _overlapFadeMs;
        }
        _log.Info($"crossfade started token={token} track={uri} fade={fade}ms fadeFrames={_overlapFadeFrames} at={pos}ms/{dur}ms");
        EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, fade));
    }

    // Promote the incoming pipeline to current with NO gap: the incoming samples have been mixed into the same ~800ms-
    // queued renderer since _overlapReleasedBase, so re-base position there (no renderer.Reset — Reset would drop the
    // queued tail). Used for a completed fade AND for a fade cut forward by a seek/reroute (§2.6).
    void PromoteOverlap()
    {
        DecodePipeline? old;
        string token; string uri; int fade;
        lock (_gate)
        {
            if (!_overlapActive || _overlapIncoming is null)
            {
                _overlapActive = false;
                return;
            }
            old = _current;
            _current = _overlapIncoming;
            _overlapIncoming = null;
            _overlapActive = false;
            _renderBaseFrames = _overlapReleasedBase;
            _fileIdHex = _current.FileIdHex;
            token = _overlapToken; uri = _overlapTrackUri; fade = _overlapFadeMs;
            _overlapToken = ""; _overlapTrackUri = ""; _overlapFadeMs = 0;
        }
        old?.Dispose();
        _log.Info($"crossfade completed token={token} track={uri}");
        EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Completed, token, uri, PositionMs, fade));
    }

    // Current reached natural EOF WITHOUT an active fade. If an incoming source is ready, hand off gaplessly (§2.4):
    // same format → keep feeding the one renderer (no gap); different format → drain the tail, re-init, one small gap
    // (identical to today's non-crossfade change). Otherwise report Missed (if an incoming was expected) then TrackFinished.
    bool HandleCurrentEof(DecodePipeline current, CancellationToken ct)
    {
        int rate = _renderer.SampleRate;
        long trackFrames = _renderer.ReleasedFrames - Interlocked.Read(ref _renderBaseFrames);
        long ms = rate > 0 && current.Channels > 0 ? trackFrames * 1000L / rate : 0;
        long dur = current.DurationMs;
        long pct = dur > 0 ? ms * 100 / dur : 0;

        // Claim the incoming out of the field under the gate so a concurrent CancelIncoming can no longer dispose it out
        // from under the hand-off (it sees _incoming == null → NotFound, harmless).
        DecodePipeline? inc; string token, uri; bool bodySupplied;
        lock (_gate)
        {
            inc = _incoming; token = _incomingToken; uri = _incomingTrackUri; bodySupplied = _incomingBodySupplied;
            _incoming = null;
            ClearIncomingMetadataLocked();
        }

        if (inc is not null && bodySupplied && inc.CanBuildDecoder && !inc.Faulted)
        {
            try
            {
                if (!inc.DecoderBuilt) inc.BuildDecoder();
                GaplessPromote(current, inc, token, uri, ct);
                return true;
            }
            catch (Exception ex)
            {
                _log.Info($"gapless promote failed token={token}: {ex.Message}");
                try { inc.Dispose(); } catch { }
                // fall through to the natural-finish path (a Missed was not the pre-existing behavior on promote failure)
            }
        }

        _log.Info($"decode ended naturally at ~{ms}ms" + (dur > 0 ? $" of {dur}ms ({pct}%){(pct < 25 ? " ⚠ HEAD-ONLY: body never decoded (check CDN/key above)" : "")}" : ""));
        while (!ct.IsCancellationRequested && _renderer.PlayedFrames < _renderer.ReleasedFrames) Thread.Sleep(20);

        if (inc is not null)
        {
            string reason = bodySupplied ? "next decoder not prebuffered" : "next body not supplied";
            try { inc.Dispose(); } catch { }
            EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Missed, token, uri, PositionMs, 0, reason));
        }

        _playing = false;
        RaiseState();
        TrackFinished?.Invoke();
        return false;
    }

    // The claimed incoming is owned locally (already removed from _incoming) so no other thread can touch it here.
    void GaplessPromote(DecodePipeline current, DecodePipeline inc, string token, string uri, CancellationToken ct)
    {
        bool sameFormat = inc.SampleRate == current.SampleRate && inc.Channels == current.Channels;
        EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, 0));

        if (sameFormat)
        {
            // Keep the same ~800ms-queued renderer running; the incoming decoder simply starts feeding the next frames.
            long baseFrames = _renderer.ReleasedFrames;
            DecodePipeline? old;
            lock (_gate)
            {
                old = _current;
                _current = inc;
                _renderBaseFrames = baseFrames;
                _fileIdHex = inc.FileIdHex;
            }
            old?.Dispose();
            _log.Info($"gapless hand-off (same format) token={token} track={uri}");
        }
        else
        {
            // Drain the queued tail so nothing is dropped, then re-open the renderer at the incoming format (one small
            // gap, exactly like a normal track change).
            while (!ct.IsCancellationRequested && _renderer.PlayedFrames < _renderer.ReleasedFrames) Thread.Sleep(20);
            InitRenderer(inc);
            DecodePipeline? old;
            lock (_gate)
            {
                old = _current;
                _current = inc;
                _renderBaseFrames = 0;
                _fileIdHex = inc.FileIdHex;
            }
            _rendererPrimed = false;
            if (_playing) _renderer.Start();
            old?.Dispose();
            _log.Info($"gapless hand-off (format change) token={token} track={uri}");
        }
        EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Completed, token, uri, PositionMs, 0));
    }

    void ClearIncomingMetadataLocked()
    {
        _incomingToken = "";
        _incomingTrackUri = "";
        _incomingDurationMs = 0;
        _incomingFadeMs = 0;
        _incomingBodySupplied = false;
    }

    // Re-open the output on the output thread (plan §A5). Position is preserved via the seek machinery (re-open + internal
    // seek-to-last-heard) so up-to-800ms of queued-but-unheard PCM isn't skipped.
    void ApplyReroute(PendingReroute reroute, DecodePipeline current)
    {
        if (reroute.PauseFirst) { _playing = false; _renderer.Pause(); RaiseState(); }
        long lastHeardMs = PositionMs;
        try
        {
            _renderer.Init(current.SampleRate, current.Channels, RendererBufferMs, reroute.DeviceId);
            _router.NotifyOpened(_renderer.OpenedDeviceId, _renderer.OpenedAsFallback);
            ApplyPostInitSession();
            _renderBaseFrames = 0;
            Interlocked.Exchange(ref _pendingSeekMs, lastHeardMs);   // restart-after-prebuffer at the last heard position
            _rendererPrimed = false;
            _log.Info($"audio.device re-init device={_renderer.OpenedDeviceId ?? "(default)"} fallback={_renderer.OpenedAsFallback} resumeAt={lastHeardMs}ms");
        }
        catch (AudioDeviceInvalidatedException ex) { _log.Info($"audio.device re-init failed 0x{ex.Hr:X8}"); _router.ReportOpenFailed(ex.Hr); }
        catch (Exception ex) { _log.Info("audio.device re-init failed: " + ex.Message); _router.ReportOpenFailed(-1); }
    }

    void StopOutput(bool waitFully = false)
    {
        CancellationTokenSource? cts; Thread? thread; DecodePipeline? current; DecodePipeline? incoming; DecodePipeline? overlap;
        lock (_gate)
        {
            cts = _outputCts;
            thread = _outputThread;
            current = _current;
            incoming = _incoming;
            overlap = _overlapIncoming;
            _outputCts = null;
            _outputThread = null;
            _current = null;
            _incoming = null;
            _overlapIncoming = null;
            _overlapActive = false;
            _overlapToken = ""; _overlapTrackUri = ""; _overlapFadeMs = 0;
            _pendingSeekMs = -1;
            ClearIncomingMetadataLocked();
        }
        try { cts?.Cancel(); } catch { }
        try { current?.Dispose(); } catch { }   // dispose the streams to unblock any in-flight blocking read
        try { incoming?.Dispose(); } catch { }
        try { overlap?.Dispose(); } catch { }
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            try
            {
                if (waitFully)
                {
                    if (!thread.Join(2000))
                        _log.Info("output stop timed out after 2000ms during disposal; proceeding with renderer/CCW teardown");
                }
                else if (!thread.Join(120))
                    _log.Info("output stop timed out after 120ms; continuing with streams disposed");
            }
            catch { }
        }
        try { _renderer.Reset(); } catch { }
        _rendererPrimed = false;
        ClearNetworkRecovery(raiseState: false);
        cts?.Dispose();
    }

    void EmitTransition(AudioTransitionSignal signal)
    {
        try { Transition?.Invoke(signal); }
        catch (Exception ex) { _log.Info("audio transition dispatch failed: " + ex.Message); }
    }

    void RaiseState()
    {
        var kind = _prebuffering ? AudioHostSignalKind.Prebuffering
            : _buffering ? AudioHostSignalKind.Buffering
            : _recoveryKind != PlaybackRecoveryKind.None ? AudioHostSignalKind.Recovering
            : _playing ? AudioHostSignalKind.Playing
            : AudioHostSignalKind.Paused;
        State?.Invoke(new AudioHostSignal(kind, PositionMs, _playing, _buffering, _prebuffering, _recoveryKind));
    }

    DecodePipeline CreatePipeline()
    {
        var pipeline = new DecodePipeline(_log, _nativeDecryptorFactory, _bodyDisk, _http);
        pipeline.NetworkRecovery += OnPipelineNetworkRecovery;
        return pipeline;
    }

    void OnPipelineNetworkRecovery(DecodePipeline pipeline, AudioNetworkRecoveryEvent e)
    {
        bool audible;
        lock (_gate) audible = ReferenceEquals(pipeline, _current) || (_overlapActive && ReferenceEquals(pipeline, _overlapIncoming));
        if (!audible) return;

        switch (e.Stage)
        {
            case AudioNetworkRecoveryStage.Started:
                _recoveryPipeline = pipeline;
                _recoveryKind = PlaybackRecoveryKind.Network;
                _networkDataAvailable = false;
                _networkRecoveryQueuedFrames = 0;
                _networkRecoveryDrained = _buffering || (!_rendererPrimed && _prebuffering);
                StartRecoveryMonitor();
                _log.Info($"audio.network_recovery.visible file={pipeline.FileIdHex} position={PositionMs}ms queued={Math.Max(0, _renderer.ReleasedFrames - _renderer.PlayedFrames)}frames");
                RaiseState();
                break;
            case AudioNetworkRecoveryStage.Recovered:
                if (ReferenceEquals(_recoveryPipeline, pipeline))
                {
                    _networkDataAvailable = true;
                    if (!_playing) CompleteNetworkRecovery();
                }
                break;
            case AudioNetworkRecoveryStage.Exhausted:
                // The typed exception is about to leave the blocking read and is surfaced by OutputThreadMain.
                break;
            case AudioNetworkRecoveryStage.Cancelled:
                if (ReferenceEquals(_recoveryPipeline, pipeline)) ClearNetworkRecovery(raiseState: false);
                break;
        }
    }

    void StartRecoveryMonitor()
    {
        lock (_gate)
        {
            _recoveryMonitor ??= new Timer(_ => MonitorRecoveryDrain(), null, 0, 50);
        }
    }

    void MonitorRecoveryDrain()
    {
        if (_disposed || _recoveryKind != PlaybackRecoveryKind.Network || _networkRecoveryDrained || !_playing || !_rendererPrimed)
            return;
        long released = _renderer.ReleasedFrames;
        if (_renderer.PlayedFrames < released) return;
        _networkRecoveryDrained = true;
        _networkRecoveryQueuedFrames = 0;
        _buffering = true;
        try { _renderer.Pause(); } catch { }
        _log.Info($"audio.network_recovery.buffer_drained file={_recoveryPipeline?.FileIdHex ?? _fileIdHex} position={PositionMs}ms");
        RaiseState();
    }

    void CompleteNetworkRecovery()
    {
        var file = _recoveryPipeline?.FileIdHex ?? _fileIdHex;
        ClearNetworkRecovery(raiseState: false);
        _buffering = false;
        _log.Info($"audio.network_recovery.playback_resumed file={file} position={PositionMs}ms playing={_playing}");
        RaiseState();
    }

    void ClearNetworkRecovery(bool raiseState)
    {
        _recoveryKind = PlaybackRecoveryKind.None;
        _networkRecoveryDrained = false;
        _networkDataAvailable = false;
        _networkRecoveryQueuedFrames = 0;
        _recoveryPipeline = null;
        Timer? monitor;
        lock (_gate) { monitor = _recoveryMonitor; _recoveryMonitor = null; }
        try { monitor?.Dispose(); } catch { }
        if (raiseState) RaiseState();
    }

    void EmitTerminalFailure(Exception ex, DecodePipeline? pipeline)
    {
        if (Interlocked.Exchange(ref _terminalFaultEmitted, 1) != 0) return;
        _playing = false;
        _buffering = false;
        _prebuffering = false;
        ClearNetworkRecovery(raiseState: false);
        try { _renderer.Pause(); } catch { }

        var range = FindRangeFailure(ex);
        var reason = range?.Reason
            ?? (ex is AudioPlaybackException playback ? playback.Reason
                : ex is CdnPermanentException permanent ? permanent.Reason
                : ex is HttpRequestException or TaskCanceledException ? AudioKeyFailureReason.Network
                : AudioKeyFailureReason.EmulationFault);
        string detail = ex.Message;
        long terminalPosition = PositionMs;
        _log.Info($"audio.output.terminal file={pipeline?.FileIdHex ?? _fileIdHex} reason={reason} position={terminalPosition}ms detail={detail}");
        try { pipeline?.Dispose(); } catch { }
        State?.Invoke(AudioHostSignal.Fault(terminalPosition, reason, detail));
    }

    static AudioRangeFetchException? FindRangeFailure(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AudioRangeFetchException range) return range;
            ex = ex.InnerException;
        }
        return null;
    }

    static long TicksToMs(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Dispose();
        ClearNetworkRecovery(raiseState: false);
        try { _router.Dispose(); } catch { }
        try { _monitor.Dispose(); } catch { }
        StopOutput(waitFully: true);
        try { _renderer.UnregisterSessionEvents(); } catch { }
        _renderer.Dispose();
        if (_sessionSinkPtr != IntPtr.Zero) { try { Marshal.Release(_sessionSinkPtr); } catch { } }
        GC.KeepAlive(_sessionSink);
        if (_ownsHttp) _http.Dispose();
    }
}

sealed class AudioThreadPriority : IDisposable
{
    readonly WaveeLogger _log;
    readonly string _fileIdHex;
    IntPtr _handle;

    AudioThreadPriority(WaveeLogger log, string fileIdHex, IntPtr handle)
    {
        _log = log;
        _fileIdHex = fileIdHex;
        _handle = handle;
    }

    public static AudioThreadPriority? TryEnter(WaveeLogger log, string fileIdHex)
    {
        try
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }

            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            var task = "Pro Audio";
            if (handle == IntPtr.Zero)
            {
                var firstError = Marshal.GetLastWin32Error();
                taskIndex = 0;
                handle = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
                task = "Audio";
                if (handle == IntPtr.Zero)
                {
                    log.Info($"audio thread priority {fileIdHex}: MMCSS registration failed proAudioError={firstError} audioError={Marshal.GetLastWin32Error()} managedPriority={Thread.CurrentThread.Priority}");
                    return null;
                }
            }

            if (!AvSetMmThreadPriority(handle, 1))
                log.Info($"audio thread priority {fileIdHex}: MMCSS task={task} registered but priority raise failed error={Marshal.GetLastWin32Error()} managedPriority={Thread.CurrentThread.Priority}");
            else
                log.Info($"audio thread priority {fileIdHex}: MMCSS task={task} priority=High managedPriority={Thread.CurrentThread.Priority}");

            return new AudioThreadPriority(log, fileIdHex, handle);
        }
        catch (Exception ex)
        {
            log.Info($"audio thread priority {fileIdHex}: setup failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) return;
        _handle = IntPtr.Zero;
        try
        {
            if (!AvRevertMmThreadCharacteristics(handle))
                _log.Info($"audio thread priority {_fileIdHex}: MMCSS revert failed error={Marshal.GetLastWin32Error()}");
        }
        catch { }
    }

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
