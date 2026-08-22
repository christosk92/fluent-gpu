using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Pal;
using MediaTrackKind = FluentGpu.Media.TrackKind;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// A protected (PlayReady/CDM) <see cref="IMediaSession"/> — the DRM counterpart of the clear MF <c>MfMediaSession</c>.
/// It drives an <see cref="IProtectedVideoPlayer"/> (the in-process native CDM in production; a fake in tests) and maps its
/// worker-thread snapshot state onto the player's <see cref="MediaSignalSink"/> ON THE UI/pump thread (so the sole-writer
/// contract holds). The produced PROTECTED DirectComposition handle binds through the SAME <c>VideoBinding.Bind</c> point
/// as clear video — nothing downstream changes. A CDM/license shortfall surfaces as a typed
/// <see cref="MediaErrorCategory.Drm"/> error (never a silent black frame).
/// </summary>
public sealed class ProtectedMediaSession : IMediaSession, IVideoSurfaceSession, IVideoPumpSource
{
    private readonly IProtectedVideoPlayer _player;
    private readonly ProtectedVideoRequest _request;
    private readonly MediaOpenOptions _opts;
    private readonly MediaLocus _locus;

    private MediaSignalSink? _sink;
    private bool _disposed;
    private bool _started;
    private bool _playRequested;   // UI-thread play intent (the native MTA loop reconciles the actual transport level)

    // Published/realized state (UI thread, via the pump).
    // The protected DComp swapchain is created once at open and keeps that physical size across representation
    // switches. The selected representation's resolution is DISPLAY metadata; feeding it to SetContentSize would lie
    // about the backing surface and make the hole and video visual scale by different factors (the giant Mica gutter at
    // 1080p). Keep the two concepts separate.
    private SizeI _surfaceSize = SizeI.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _commandsPublished;
    private bool _errorPublished;
    private double _volume = 1.0;
    private bool _muted;
    private readonly ProtectedTrackDescriptor? _videoTrack;
    private readonly QualityVariant[] _qualityVariants = Array.Empty<QualityVariant>();
    private readonly AdaptiveBitrateController? _abr;
    private int _policyMaxHeight = int.MaxValue;
    private int _viewportMaxHeight = int.MaxValue;
    private QualitySelection _qualitySelection = QualitySelection.Auto;
    private QualityVariant? _activeQuality;
    private string? _pendingRepresentationId;
    private long _pendingRepresentationTicks;
    private long _lastBytesDownloaded;
    private long _lastDownloadElapsedMs;
    private long _lastGrowthTicks;
    private long _lastAbrTicks;

    // Throughput sample accumulation. The pump observes the native byte counter every 250ms, and one 250ms slice of a
    // 2 Mbps stream is ~62 KB — just under the estimator's 64 KB noise floor, so sampling per pump would throw away
    // almost every real measurement. Accumulate across pumps instead and emit ONE sample once it is big enough to mean
    // something. Idle slices (no byte growth) are never charged to the pending sample: charging wall-clock time in
    // which nothing transferred is what makes a wall-clock estimate read low.
    private long _pendingSampleBytes;
    private long _pendingSampleMs;
    private long _lastSampleBytes;
    private long _lastSampleMs;
    private double _lastSampleKbps;

    // Seek intent (the optimistic publish + the republish-suppression window). The native transport acknowledges a
    // seek asynchronously — seconds, on a protected source. Publishing the target only AFTER that ack made the seek
    // bar snap back to the stale playhead for the whole round-trip, and the post-await write ran on a THREAD-POOL
    // thread, i.e. a signal write off the pump, breaking the sole-writer contract documented at the top of this file.
    // The target is now published SYNCHRONOUSLY on the calling (UI/pump) thread; the ack continuation only sets
    // _seekSettlePending and asks for a pump, and the pump does every signal write.
    private bool _seekPending;
    private long _seekTargetMs;
    private long _seekDeadlineTicks;
    private int _seekSettlePending;
    private long _seekAckTicks;
    private const int SeekSuppressMs = 6_000;          // outer bound on the suppression window
    private const long SeekReachedToleranceMs = 750;   // native position considered "arrived" within this of the target

    // Session-level start watchdog (belt-and-suspenders around the player's own): guarantees a terminal Failed even if
    // the underlying player never reports Error. A CONSTANT 90s, matching the player's own budget and sitting above
    // every native ceiling it supervises (30s licence + 45s CANPLAY + 12s handle). No environment override.
    private const int StartTimeoutMs = 90_000;
    private long _startTicks;
    private bool _watchdogFired;

    // The desktop PlayReady backend exposes a native snapshot rather than a media-engine event callback. Poll it at a
    // deliberately low cadence only while opening, buffering, playing, or settling a transport command. That preserves
    // protected-session state/position progress without turning every panel frame into a UI-thread video repaint.
    private const int PumpPollMs = 250;
    private const int TransportSettlePollMs = 1_000;
    private Timer? _pumpPoll;
    private bool _pumpPollActive;
    private long _pollUntilTicks;

    /// <inheritdoc/>
    public event Action? PumpRequested;

    /// <summary>Create a protected session over <paramref name="player"/> for <paramref name="request"/>.</summary>
    public ProtectedMediaSession(IProtectedVideoPlayer player, ProtectedVideoRequest request, MediaOpenOptions opts)
    {
        _player = player;
        _request = request;
        _opts = opts;
        _playRequested = !opts.StartPaused;
        _locus = new MediaLocus(null, request.Source, null, null, null);
        _videoTrack = FindDefaultTrack(request.Catalog, MediaTrackKind.Video);
        if (_videoTrack is { Representations.Count: > 0 })
        {
            _qualityVariants = new QualityVariant[_videoTrack.Representations.Count];
            for (int i = 0; i < _qualityVariants.Length; i++)
                _qualityVariants[i] = _videoTrack.Representations[i].Quality;
            _abr = opts.Abr as AdaptiveBitrateController ?? new AdaptiveBitrateController();
            _policyMaxHeight = _abr.MaxHeight;
            _qualitySelection = _abr.Selection;
            _activeQuality = FindInitialQuality(_videoTrack, request.InitUrl);
            for (int i = 0; i < _qualityVariants.Length; i++)
                if (string.Equals(_qualityVariants[i].Id, _activeQuality?.Id, StringComparison.Ordinal))
                { _abr.SeedCurrent(i); break; }
        }
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        sink.PlayRequested(!_opts.StartPaused);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        PublishCatalog(sink);
        StartOnce();
        if (!_qualitySelection.IsAuto && _qualitySelection.VariantId is { } initialPin)
            RequestRepresentation(initialPin);
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
    }

    private static void PumpPollTick(object? state) => ((ProtectedMediaSession)state!).RequestPump();

    private void RequestPump()
    {
        if (_disposed) return;
        try { PumpRequested?.Invoke(); } catch { }
    }

    private void KeepPollingFor(int durationMs)
    {
        if (_disposed) return;
        _pollUntilTicks = Math.Max(_pollUntilTicks, Environment.TickCount64 + durationMs);
        SetPumpPoll(true);
    }

    private void SetPumpPoll(bool active)
    {
        if (_disposed || _pumpPollActive == active) return;
        _pumpPollActive = active;
        if (active)
        {
            _pumpPoll ??= new Timer(PumpPollTick, this, Timeout.Infinite, Timeout.Infinite);
            _pumpPoll.Change(0, PumpPollMs);
        }
        else
        {
            _pumpPoll?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private bool ShouldPoll(ProtectedVideoState state)
        => !_disposed && !_errorPublished &&
            (Environment.TickCount64 < _pollUntilTicks
             || _seekPending   // a seek in flight must keep the pump alive until the native position crosses over
             || state is ProtectedVideoState.Launching or ProtectedVideoState.Connecting or ProtectedVideoState.Loading
                 or ProtectedVideoState.Licensed or ProtectedVideoState.Buffering
             || (_playRequested && state is not (ProtectedVideoState.Error or ProtectedVideoState.Ended or ProtectedVideoState.Stopped)));

    private void StartOnce()
    {
        if (_started) return;
        _started = true;
        _startTicks = Environment.TickCount64;
        _lastGrowthTicks = _startTicks;
        _player.Start(_request);   // non-blocking; the native CDM/decode loop runs on its own MTA thread
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _player.HasSurface && !_surfaceSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _surfaceSize, IsHdr: false)
            : VideoDelivery.None;

    // ── transport (idempotent; accepted synchronously; the pump realizes state) ──────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        StartOnce();
        _playRequested = true;
        _sink?.PlayRequested(true);
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
        return _player.PlayAsync();
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _sink?.PlayRequested(false);
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
        return _player.PauseAsync();
    }

    /// <inheritdoc/>
    public void PublishSeekIntent(TimeSpan target)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;
        long ms = ClampToDuration(target);

        // Publish the TARGET now, on the calling (UI/pump) thread, BEFORE any await. The native ack is a multi-second
        // round-trip on a protected source; publishing only afterwards is what made the scrubber snap back to the
        // stale playhead for seconds after every drag.
        _seekTargetMs = ms;
        _seekPending = true;
        _seekDeadlineTicks = Environment.TickCount64 + SeekSuppressMs;
        _seekAckTicks = 0;
        Publish(sink, PlaybackState.Buffering);
        sink.Position(TimeSpan.FromMilliseconds(ms));
        sink.Buffering(new BufferingInfo(BufferingReason.Seeking, -1, TimeSpan.Zero, TimeSpan.Zero, false));
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return ValueTask.CompletedTask;
        long ms = ClampToDuration(to);
        PublishSeekIntent(to);
        // A seek does NOT change the network, so the throughput history survives it; only the ladder's vote/probe
        // state is stale (the buffer is about to be discarded and refilled).
        _abr?.ResetForSeek();
        // The player logs the issued seek (position + mode) on the same timeline, so this path deliberately does NOT
        // add a second line: a drag issues one throttled seek every 200ms and each log line is a file append on the
        // UI thread.
        try
        {
            return new ValueTask(AwaitSeekAckAsync(_player.SeekAsync(ms, mode)));
        }
        catch (Exception e)
        {
            // A synchronous backend refusal is not a user-visible failure: the pump reconciles from the snapshot.
            _player.LogDiagnostic($"seek {ms}ms was refused synchronously: {e.Message}");
            Volatile.Write(ref _seekSettlePending, 1);
            RequestPump();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Await the backend's seek acknowledgement WITHOUT touching a signal. This continuation runs on a
    /// thread-pool thread, so it may only set a flag and ask for a pump — the pump (UI thread) does every write, which
    /// is what keeps the sole-writer contract this class documents at the top intact.</summary>
    private async Task AwaitSeekAckAsync(ValueTask pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch { /* superseded or timed out — a stale ack nobody is waiting on, never a user-visible failure */ }
        Volatile.Write(ref _seekSettlePending, 1);
        RequestPump();
    }

    private long ClampToDuration(TimeSpan to)
    {
        double hi = _duration > TimeSpan.Zero ? _duration.TotalMilliseconds : double.MaxValue;
        return (long)Math.Clamp(to.TotalMilliseconds, 0.0, hi);
    }

    /// <inheritdoc/>
    public void SetRate(double rate)
    {
        if (_disposed) return;
        _player.SetRate((float)rate);
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
    }
    /// <inheritdoc/>
    public void SetVolume(double volume)
    {
        if (_disposed) return;
        _volume = Math.Clamp(volume, 0, 1);
        _player.SetVolume(_muted ? 0f : (float)_volume);
    }
    /// <inheritdoc/>
    public void SetMuted(bool muted)
    {
        if (_disposed) return;
        _muted = muted;
        _player.SetVolume(_muted ? 0f : (float)_volume);
        _sink?.Muted(muted);
    }

    /// <inheritdoc/>
    public async ValueTask SelectQualityAsync(QualitySelection selection)
    {
        if (_disposed || _videoTrack is null) return;
        if (!selection.IsAuto && FindRepresentation(_videoTrack, selection.VariantId) is null)
            throw new ArgumentOutOfRangeException(nameof(selection), "The protected manifest does not contain that representation.");

        QualitySelection previous = _qualitySelection;
        _qualitySelection = selection;
        if (_abr is not null) _abr.Selection = selection;
        _sink?.QualitySelection(selection, _activeQuality);
        if (!selection.IsAuto && selection.VariantId is { } id)
        {
            _pendingRepresentationId = id;
            try
            {
                await _player.SelectVideoRepresentationAsync(id).ConfigureAwait(false);
            }
            catch
            {
                _pendingRepresentationId = null;
                _qualitySelection = previous;
                if (_abr is not null) _abr.Selection = previous;
                _sink?.QualitySelection(previous, _activeQuality);
                throw;
            }
        }
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
    }

    /// <inheritdoc/>
    public ValueTask SelectTrackAsync(MediaTrack? track)
    {
        if (_disposed || track is null || _request.Catalog is null) return ValueTask.CompletedTask;
        for (int i = 0; i < _request.Catalog.Tracks.Count; i++)
            if (_request.Catalog.Tracks[i].Id == track.Id && _request.Catalog.Tracks[i].Kind == track.Kind)
                return _player.SelectTrackAsync(track.Id);
        throw new ArgumentOutOfRangeException(nameof(track), "The protected manifest does not contain that track.");
    }

    /// <summary>The floor the laid-out viewport height is clamped to before it becomes an ABR cap.
    /// <para>THE BUG THIS EXISTS FOR ("Auto · 240p", and the frame freeze the resulting switch caused): the element
    /// pushes its laid-out VIDEO HEIGHT IN DIP every pump, and real video surfaces are small — 191 DIP for the docked
    /// rail, 202 DIP for the pop-out, 135 DIP at the pop-out minimum. Every one of those is BELOW every rung a
    /// manifest offers (240p and up), so the raw viewport height filtered the entire ladder away and Auto collapsed to
    /// the bottom rung on any bandwidth. A viewport is a hint about what is worth downloading, not a licence to starve
    /// the ladder — so it can only ever cap the climb at 720p or above.</para></summary>
    private const int MinViewportCapHeight = 720;

    /// <inheritdoc/>
    public void SetAdaptiveViewportHeight(int height)
    {
        if (_abr is null) return;
        // A zero/negative height is "not laid out yet", and a height arriving while a representation switch is still
        // in flight describes a surface that is mid-replacement — neither is a real viewport, and acting on either
        // re-caps the ladder from a transient measurement.
        if (height <= 0 || _pendingRepresentationId is not null) return;
        _viewportMaxHeight = Math.Max(height, MinViewportCapHeight);
        _abr.MaxHeight = Math.Min(_policyMaxHeight, _viewportMaxHeight);
    }

    /// <inheritdoc/>
    public void SetAdaptiveMaxHeight(int height)
    {
        if (_abr is null) return;
        _policyMaxHeight = height > 0 ? height : int.MaxValue;
        _abr.MaxHeight = Math.Min(_viewportMaxHeight, _policyMaxHeight);
    }

    // ── the UI-thread pump (state mapping + the composited-surface handoff) ───────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;

        // Advance the native snapshot + bind the PROTECTED DComp handle (value-gated inside the player).
        _player.Pump(binding);
        var pv = _player.State.Value;
        UpdateAdaptiveState(sink);

        // 1. Terminal CDM/DRM error → typed MediaError (published once). Never a silent drop.
        if (pv == ProtectedVideoState.Error)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                sink.Error(new MediaError(MediaErrorCategory.Drm,
                    _player.Error.Value ?? "Protected playback failed (CDM/license).", null, _locus, MediaRecovery.NeedsLicense));
                Publish(sink, PlaybackState.Failed);
            }
            SetPumpPoll(false);
            return;
        }

        // 1b. Session watchdog — guarantee a terminal failure even if the player never reports Error (e.g. an OLD native
        // DLL that only LOGS a rejected license). If we asked to play and are still merely Opening/Buffering past the start
        // budget with no natural size, surface the same typed DRM failure instead of an eternal "Starting playback…".
        if (!_watchdogFired && !_errorPublished && _playRequested && _surfaceSize.IsEmpty
            && _publishedState is PlaybackState.Opening or PlaybackState.Buffering
            && Environment.TickCount64 - _startTicks > StartTimeoutMs)
        {
            _watchdogFired = true;
            _errorPublished = true;
            _player.LogDiagnostic($"session watchdog TIMED OUT after {Environment.TickCount64 - _startTicks}ms " +
                                  $"of a {StartTimeoutMs}ms budget (state={pv}, no natural size) — surfacing Failed");
            sink.Error(new MediaError(MediaErrorCategory.Drm,
                _player.Error.Value ?? $"Protected video TIMED OUT after {StartTimeoutMs / 1000}s: the backend never " +
                    "reported a natural size. This is a timeout, not a licence rejection (a rejected licence is " +
                    "reported by the licence relay). " +
                    "See %LOCALAPPDATA%\\FluentGpu\\PlayReady\\desktop-playready.log.",
                null, _locus, MediaRecovery.NeedsLicense));
            Publish(sink, PlaybackState.Failed);
            SetPumpPoll(false);
            return;
        }

        // 2. Natural size / duration / commands once the CDM reports them.
        var ns = _player.NaturalSize.Value;
        if (ns.Width > 0 && (_surfaceSize.Width != (int)ns.Width || _surfaceSize.Height != (int)ns.Height))
        {
            _surfaceSize = new SizeI((int)ns.Width, (int)ns.Height);
            SizeI displaySize = _activeQuality?.Resolution is { IsEmpty: false } selected
                ? selected
                : _surfaceSize;
            sink.NaturalSize(displaySize);
            if (!_commandsPublished)
            {
                _commandsPublished = true;
                var commands = MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate;
                if (_player.SupportsAdaptiveSelection && _videoTrack is { Representations.Count: > 1 })
                    commands |= MediaCommandFlags.SelectVideoQuality;
                if (_player.SupportsTrackSelection && CountTracks(MediaTrackKind.Audio) > 1)
                    commands |= MediaCommandFlags.SelectAudioTrack;
                if (_player.SupportsTrackSelection && CountTracks(MediaTrackKind.Video) > 1)
                    commands |= MediaCommandFlags.SelectVideoTrack;
                sink.Commands(commands);
            }
        }
        long durMs = _player.DurationMs.Value;
        if (durMs > 0 && (long)_duration.TotalMilliseconds != durMs)
        {
            _duration = TimeSpan.FromMilliseconds(durMs);
            sink.Duration(_duration);
        }

        // 3. Composited-surface handoff (Path A) — place the (already-bound) protected surface at the video rect.
        if (binding.IsValid && _player.HasSurface)
        {
            binding.SetContentSize(_surfaceSize);   // physical swapchain size; representation resolution is metadata
            binding.Place(videoRect);
            binding.SetVisible(true);
        }

        // 4. State + position. The play/pause LEVEL is reconciled natively (the MTA loop re-asserts Play until the clock
        // advances — boot-drop + resume both covered — and never clobbers a Seek, since seek has its own slot). The old
        // managed 60Hz Play re-assert lived here and is gone: it filled the single native command slot and overwrote
        // Seek/Pause issued in the same 80ms window (the seek + resume-after-pause failures).
        long posMs = _player.PositionMs.Value;

        // 4a. Drain a completed seek acknowledgement HERE — the continuation that observed it runs on a thread-pool
        // thread and is forbidden from writing signals, so it only raises this flag and asks for a pump.
        long tick = Environment.TickCount64;
        if (Volatile.Read(ref _seekSettlePending) != 0)
        {
            Volatile.Write(ref _seekSettlePending, 0);
            if (_seekAckTicks == 0) _seekAckTicks = tick;
            sink.SettleTransport();
        }

        // 4b. While a seek is in flight, the native playhead is still the PRE-seek value. Republishing it every 250ms
        // would undo the optimistic target published by PublishSeekIntent and snap the scrubber back — the exact bug
        // the optimistic publish exists to fix. Hold both the position and the state until the seek has demonstrably
        // landed, which is any of:
        //   • the native position reached the target (an EXACT seek), or
        //   • the native transport acknowledged and has had one poll interval to publish its new (possibly
        //     keyframe-snapped, so NOT equal to the target) playhead, or
        //   • the outer window expired — so a dropped acknowledgement can never wedge the transport.
        if (_seekPending
            && (Math.Abs(posMs - _seekTargetMs) <= SeekReachedToleranceMs
                || (_seekAckTicks != 0 && tick - _seekAckTicks >= PumpPollMs)
                || tick >= _seekDeadlineTicks))
        {
            _player.LogDiagnostic($"seek landed: target={_seekTargetMs}ms native={posMs}ms " +
                                  $"{(_seekAckTicks == 0 ? "(no ack — window expired)" : "acked")} after " +
                                  $"{SeekSuppressMs - Math.Max(0, _seekDeadlineTicks - tick)}ms");
            _seekPending = false;
            _seekAckTicks = 0;
        }

        Publish(sink, _seekPending ? PlaybackState.Buffering : MapState(pv));
        if (!_seekPending) sink.Position(TimeSpan.FromMilliseconds(posMs));
        SetPumpPoll(ShouldPoll(pv));
    }

    private void PublishCatalog(MediaSignalSink sink)
    {
        sink.ResetTracks();
        if (_request.Catalog is null) return;
        for (int i = 0; i < _request.Catalog.Tracks.Count; i++)
        {
            var track = _request.Catalog.Tracks[i];
            if (track.Representations.Count == 0) continue;
            sink.Track(track.Id, track.Kind, track.Language, track.Label, track.Role,
                track.Representations[0].Quality.Codec, track.IsDefault);
        }
        if (_videoTrack is not null)
        {
            sink.QualityVariants(_qualityVariants);
            sink.QualitySelection(_qualitySelection, _activeQuality);
        }
    }

    private void UpdateAdaptiveState(MediaSignalSink sink)
    {
        if (_videoTrack is null || _abr is null || _qualityVariants.Length == 0) return;
        long now = Environment.TickCount64;
        long bytes = _player.BytesDownloaded;
        long downloadMs = _player.DownloadElapsedMs;

        if (bytes < _lastBytesDownloaded || downloadMs < _lastDownloadElapsedMs)
        {
            // The backend rebased its counters (a new open, or a representation switch) — never sample across that.
            _lastBytesDownloaded = bytes;
            _lastDownloadElapsedMs = downloadMs;
            _pendingSampleBytes = 0;
            _pendingSampleMs = 0;
            _lastGrowthTicks = now;
        }
        else if (bytes > _lastBytesDownloaded)
        {
            // WALL-CLOCK FALLBACK. The V1 native ABI never reports DownloadElapsedMs (it stays 0 forever), and the
            // old branch order meant `_lastDownloadElapsedMs == 0` won on EVERY pump: RecordDownload was never
            // called, the estimate stayed structurally 0, and the `EstimatedKbps <= 0` guard below meant Choose never
            // ran at all — Auto was inert for the whole session. Prefer the backend's transfer-only clock when it
            // moves; otherwise charge the wall time since the last slice that ACTUALLY moved bytes (idle slices are
            // never charged, so the wall-clock estimate is not dragged down by the gaps between segment fetches).
            long deltaBytes = bytes - _lastBytesDownloaded;
            long deltaMs = downloadMs > _lastDownloadElapsedMs ? downloadMs - _lastDownloadElapsedMs
                                                               : Math.Max(1, now - _lastGrowthTicks);
            _pendingSampleBytes += deltaBytes;
            _pendingSampleMs += deltaMs;
            _lastBytesDownloaded = bytes;
            _lastDownloadElapsedMs = downloadMs;
            _lastGrowthTicks = now;

            if (_abr.RecordDownload(_pendingSampleBytes, TimeSpan.FromMilliseconds(_pendingSampleMs)))
            {
                _lastSampleBytes = _pendingSampleBytes;
                _lastSampleMs = _pendingSampleMs;
                _lastSampleKbps = _pendingSampleBytes * 8.0 / _pendingSampleMs;   // bytes*8/ms == kbit/s
                _pendingSampleBytes = 0;
                _pendingSampleMs = 0;
            }
        }
        else
        {
            _lastGrowthTicks = now;   // nothing transferred this slice — do not charge it to the pending sample
        }

        string? activeId = _player.ActiveVideoRepresentationId;
        if (activeId is not null && !string.Equals(activeId, _activeQuality?.Id, StringComparison.Ordinal)
            && FindRepresentation(_videoTrack, activeId) is { } active)
        {
            _activeQuality = active.Quality;
            for (int i = 0; i < _qualityVariants.Length; i++)
                if (string.Equals(_qualityVariants[i].Id, activeId, StringComparison.Ordinal))
                { _abr.SeedCurrent(i); break; }
            if (string.Equals(_pendingRepresentationId, activeId, StringComparison.Ordinal)) _pendingRepresentationId = null;
            sink.QualitySelection(_qualitySelection, _activeQuality);
            // GUARD (the same guard the natural-size publish in PumpVideo already has). A Spotify profile that carries
            // no video_width/video_height yields SizeI.Zero; publishing that mid-playback flips MediaPlayerElement into
            // audio-only and BOTH the hole and the protected surface disappear. A missing display size means "keep the
            // one we have", never "there is no video".
            if (!active.Quality.Resolution.IsEmpty) sink.NaturalSize(active.Quality.Resolution);
            else _player.LogDiagnostic($"representation '{activeId}' has no declared resolution — " +
                                       "keeping the previous natural size (an empty one would flip to audio-only)");
        }

        // A representation switch that is never acknowledged must not wedge Auto forever: the ack path is a logged
        // no-op now, so nothing else clears the pending id.
        if (_pendingRepresentationId is not null && now - _pendingRepresentationTicks > RepresentationPendingTimeoutMs)
        {
            _player.LogDiagnostic($"representation '{_pendingRepresentationId}' never became active within " +
                                  $"{RepresentationPendingTimeoutMs}ms — releasing the ABR gate");
            _pendingRepresentationId = null;
        }

        // No throughput gate here any more. The estimator is seeded (never zero) and the decision itself is
        // buffer-gated inside Choose, so the only cadence rule is one decision per second.
        if (!_qualitySelection.IsAuto || now - _lastAbrTicks < 1_000) return;
        _lastAbrTicks = now;
        long bufferedMs = Math.Max(0, _player.ForwardBufferedMs);
        int chosen = _abr.Choose(_qualityVariants, TimeSpan.FromMilliseconds(bufferedMs));
        var pick = _qualityVariants[Math.Clamp(chosen, 0, _qualityVariants.Length - 1)];

        // Always-on per-decision line: the sample that fed the estimate, both EWMAs, the buffer, the cap, the rung and
        // WHY. This is the one line that explains an "Auto · 240p" from a field log with no repro.
        _player.LogDiagnostic(
            $"abr sample={_lastSampleBytes}B/{_lastSampleMs}ms={_lastSampleKbps:F0}kbps " +
            $"fast={_abr.FastKbps:F0} slow={_abr.SlowKbps:F0} n={_abr.ThroughputSamples} " +
            $"buffer={bufferedMs}ms cap={(_abr.MaxHeight == int.MaxValue ? "none" : _abr.MaxHeight.ToString())} " +
            $"active={_activeQuality?.Id ?? "-"} -> idx={chosen} id={pick.Id} " +
            $"{pick.Resolution.Width}x{pick.Resolution.Height}@{pick.Bitrate} why={_abr.LastDecisionReason}");

        if (!string.Equals(pick.Id, _activeQuality?.Id, StringComparison.Ordinal)
            && !string.Equals(pick.Id, _pendingRepresentationId, StringComparison.Ordinal))
            RequestRepresentation(pick.Id);
    }

    /// <summary>How long a requested-but-unacknowledged representation blocks further ABR decisions.</summary>
    private const int RepresentationPendingTimeoutMs = 12_000;

    private void RequestRepresentation(string id)
    {
        _pendingRepresentationId = id;
        _pendingRepresentationTicks = Environment.TickCount64;
        try
        {
            ValueTask pending = _player.SelectVideoRepresentationAsync(id);
            if (!pending.IsCompletedSuccessfully) _ = ObserveSwitchAsync(pending, id);
        }
        catch { _pendingRepresentationId = null; }
    }

    private async Task ObserveSwitchAsync(ValueTask pending, string id)
    {
        try { await pending.ConfigureAwait(false); }
        catch { if (string.Equals(_pendingRepresentationId, id, StringComparison.Ordinal)) _pendingRepresentationId = null; }
    }

    private int CountTracks(MediaTrackKind kind)
    {
        if (_request.Catalog is null) return 0;
        int count = 0;
        for (int i = 0; i < _request.Catalog.Tracks.Count; i++)
            if (_request.Catalog.Tracks[i].Kind == kind) count++;
        return count;
    }

    private static ProtectedTrackDescriptor? FindDefaultTrack(ProtectedAdaptiveCatalog? catalog, MediaTrackKind kind)
    {
        if (catalog is null) return null;
        ProtectedTrackDescriptor? first = null;
        for (int i = 0; i < catalog.Tracks.Count; i++)
        {
            var track = catalog.Tracks[i];
            if (track.Kind != kind) continue;
            first ??= track;
            if (track.IsDefault) return track;
        }
        return first;
    }

    private static ProtectedRepresentationDescriptor? FindRepresentation(ProtectedTrackDescriptor track, string? id)
    {
        if (id is null) return null;
        for (int i = 0; i < track.Representations.Count; i++)
            if (string.Equals(track.Representations[i].Id, id, StringComparison.Ordinal)) return track.Representations[i];
        return null;
    }

    private static QualityVariant? FindInitialQuality(ProtectedTrackDescriptor track, string? initUrl)
    {
        for (int i = 0; i < track.Representations.Count; i++)
            if (string.Equals(track.Representations[i].InitUrl, initUrl, StringComparison.Ordinal)) return track.Representations[i].Quality;
        return track.Representations.Count > 0 ? track.Representations[0].Quality : null;
    }

    private static PlaybackState MapState(ProtectedVideoState s) => s switch
    {
        ProtectedVideoState.Idle => PlaybackState.Idle,
        ProtectedVideoState.Launching or ProtectedVideoState.Connecting or ProtectedVideoState.Loading => PlaybackState.Opening,
        ProtectedVideoState.Licensed or ProtectedVideoState.Buffering => PlaybackState.Buffering,
        ProtectedVideoState.Playing => PlaybackState.Playing,
        ProtectedVideoState.Paused => PlaybackState.Paused,
        ProtectedVideoState.Ended => PlaybackState.Ended,
        ProtectedVideoState.Stopped => PlaybackState.Idle,
        ProtectedVideoState.Error => PlaybackState.Failed,
        _ => PlaybackState.Opening,
    };

    private void Publish(MediaSignalSink sink, PlaybackState state)
    {
        if (state == _publishedState) return;
        _publishedState = state;
        sink.State(state);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _sink = null;
        _pumpPollActive = false;
        _pumpPoll?.Dispose();
        _pumpPoll = null;
        PumpRequested = null;
        var player = _player;
        return new ValueTask(Task.Run(() =>
        {
            try { player.Stop(); } catch { }
            player.Dispose();
        }));
    }
}
