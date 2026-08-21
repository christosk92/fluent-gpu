using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Pal;

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
    private SizeI _naturalSize = SizeI.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _commandsPublished;
    private bool _errorPublished;
    private double _volume = 1.0;
    private bool _muted;
    private readonly ProtectedTrackDescriptor? _videoTrack;
    private readonly QualityVariant[] _qualityVariants = Array.Empty<QualityVariant>();
    private readonly AdaptiveBitrateController? _abr;
    private QualitySelection _qualitySelection = QualitySelection.Auto;
    private QualityVariant? _activeQuality;
    private string? _pendingRepresentationId;
    private long _lastBytesDownloaded;
    private long _lastThroughputTicks;
    private long _lastAbrTicks;

    // Session-level start watchdog (belt-and-suspenders around the player's own): guarantees a terminal Failed even if the
    // underlying player never reports Error. Overridable via FG_VIDEO_START_TIMEOUT_MS (ms); default 20s.
    private const int DefaultStartTimeoutMs = 20_000;
    private static readonly int StartTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("FG_VIDEO_START_TIMEOUT_MS"), out int t) && t > 0
            ? t : DefaultStartTimeoutMs;
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
        _videoTrack = FindDefaultTrack(request.Catalog, TrackKind.Video);
        if (_videoTrack is { Representations.Count: > 0 })
        {
            _qualityVariants = new QualityVariant[_videoTrack.Representations.Count];
            for (int i = 0; i < _qualityVariants.Length; i++)
                _qualityVariants[i] = _videoTrack.Representations[i].Quality;
            _abr = opts.Abr as AdaptiveBitrateController ?? new AdaptiveBitrateController();
            _qualitySelection = _abr.Selection;
            _activeQuality = FindInitialQuality(_videoTrack, request.InitUrl);
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
             || state is ProtectedVideoState.Launching or ProtectedVideoState.Connecting or ProtectedVideoState.Loading
                 or ProtectedVideoState.Licensed or ProtectedVideoState.Buffering
             || (_playRequested && state is not (ProtectedVideoState.Error or ProtectedVideoState.Ended or ProtectedVideoState.Stopped)));

    private void StartOnce()
    {
        if (_started) return;
        _started = true;
        _startTicks = Environment.TickCount64;
        _player.Start(_request);   // non-blocking; the native CDM/decode loop runs on its own MTA thread
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _player.HasSurface && !_naturalSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _naturalSize, IsHdr: false)
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
    public async ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return;
        double hi = _duration > TimeSpan.Zero ? _duration.TotalMilliseconds : double.MaxValue;
        long ms = (long)Math.Clamp(to.TotalMilliseconds, 0.0, hi);
        await _player.SeekAsync(ms).ConfigureAwait(false);
        _sink?.Position(TimeSpan.FromMilliseconds(ms));
        _sink?.SettleTransport();
        KeepPollingFor(TransportSettlePollMs);
        RequestPump();
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

        _qualitySelection = selection;
        if (_abr is not null) _abr.Selection = selection;
        _sink?.QualitySelection(selection, _activeQuality);
        if (!selection.IsAuto && selection.VariantId is { } id)
        {
            _pendingRepresentationId = id;
            await _player.SelectVideoRepresentationAsync(id).ConfigureAwait(false);
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
        if (!_watchdogFired && !_errorPublished && _playRequested && _naturalSize.IsEmpty
            && _publishedState is PlaybackState.Opening or PlaybackState.Buffering
            && Environment.TickCount64 - _startTicks > StartTimeoutMs)
        {
            _watchdogFired = true;
            _errorPublished = true;
            sink.Error(new MediaError(MediaErrorCategory.Drm,
                _player.Error.Value ?? "PlayReady license was rejected or no key became usable — video cannot start " +
                    "(see %LOCALAPPDATA%\\FluentGpu\\PlayReady\\desktop-playready.log).",
                null, _locus, MediaRecovery.NeedsLicense));
            Publish(sink, PlaybackState.Failed);
            SetPumpPoll(false);
            return;
        }

        // 2. Natural size / duration / commands once the CDM reports them.
        var ns = _player.NaturalSize.Value;
        if (ns.Width > 0 && (_naturalSize.Width != (int)ns.Width || _naturalSize.Height != (int)ns.Height))
        {
            _naturalSize = new SizeI((int)ns.Width, (int)ns.Height);
            sink.NaturalSize(_naturalSize);
            if (!_commandsPublished)
            {
                _commandsPublished = true;
                var commands = MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate;
                if (_videoTrack is { Representations.Count: > 1 }) commands |= MediaCommandFlags.SelectVideoQuality;
                if (CountTracks(TrackKind.Audio) > 1) commands |= MediaCommandFlags.SelectAudioTrack;
                if (CountTracks(TrackKind.Video) > 1) commands |= MediaCommandFlags.SelectVideoTrack;
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
            binding.SetContentSize(_naturalSize);   // scale the protected swapchain to fill videoRect (else it crops 1:1)
            binding.Place(videoRect);
            binding.SetVisible(true);
        }

        // 4. State + position. The play/pause LEVEL is reconciled natively (the MTA loop re-asserts Play until the clock
        // advances — boot-drop + resume both covered — and never clobbers a Seek, since seek has its own slot). The old
        // managed 60Hz Play re-assert lived here and is gone: it filled the single native command slot and overwrote
        // Seek/Pause issued in the same 80ms window (the seek + resume-after-pause failures).
        long posMs = _player.PositionMs.Value;

        Publish(sink, MapState(pv));
        sink.Position(TimeSpan.FromMilliseconds(posMs));
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
        if (_videoTrack is null || _abr is null) return;
        long now = Environment.TickCount64;
        long bytes = _player.BytesDownloaded;
        if (_lastThroughputTicks != 0 && bytes > _lastBytesDownloaded)
            _abr.RecordDownload(bytes - _lastBytesDownloaded, TimeSpan.FromMilliseconds(now - _lastThroughputTicks));
        if (bytes >= _lastBytesDownloaded)
        {
            _lastBytesDownloaded = bytes;
            _lastThroughputTicks = now;
        }

        string? activeId = _player.ActiveVideoRepresentationId;
        if (activeId is not null && !string.Equals(activeId, _activeQuality?.Id, StringComparison.Ordinal)
            && FindRepresentation(_videoTrack, activeId) is { } active)
        {
            _activeQuality = active.Quality;
            if (string.Equals(_pendingRepresentationId, activeId, StringComparison.Ordinal)) _pendingRepresentationId = null;
            sink.QualitySelection(_qualitySelection, _activeQuality);
            sink.NaturalSize(active.Quality.Resolution);
        }

        if (!_qualitySelection.IsAuto || now - _lastAbrTicks < 1_000) return;
        _lastAbrTicks = now;
        int chosen = _abr.Choose(_qualityVariants, TimeSpan.FromMilliseconds(Math.Max(0, _player.ForwardBufferedMs)));
        string id = _qualityVariants[Math.Clamp(chosen, 0, _qualityVariants.Length - 1)].Id;
        if (!string.Equals(id, _activeQuality?.Id, StringComparison.Ordinal)
            && !string.Equals(id, _pendingRepresentationId, StringComparison.Ordinal))
            RequestRepresentation(id);
    }

    private void RequestRepresentation(string id)
    {
        _pendingRepresentationId = id;
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

    private int CountTracks(TrackKind kind)
    {
        if (_request.Catalog is null) return 0;
        int count = 0;
        for (int i = 0; i < _request.Catalog.Tracks.Count; i++)
            if (_request.Catalog.Tracks[i].Kind == kind) count++;
        return count;
    }

    private static ProtectedTrackDescriptor? FindDefaultTrack(ProtectedAdaptiveCatalog? catalog, TrackKind kind)
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
