using System;
using System.Diagnostics;
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
/// It drives an <see cref="IProtectedVideoPlayer"/> (a <see cref="ProtectedVideoSession"/> on the process runtime in
/// production; a fake in tests) and maps its state onto the player's <see cref="MediaSignalSink"/> ON THE UI/PUMP
/// THREAD, so the sole-writer contract holds. The PROTECTED DirectComposition handle binds through the SAME
/// <c>VideoBinding.Bind</c> point as clear video. A CDM/license shortfall surfaces as a typed
/// <see cref="MediaErrorCategory.Drm"/> error, never a silent black frame.
/// <para><b>Event-driven.</b> The player raises <see cref="IProtectedVideoPlayer.PumpRequested"/> when native state
/// changed; this session forwards it as its own <see cref="PumpRequested"/> (the same <see cref="IVideoPumpSource"/>
/// contract the clear session uses) and does every signal write in <see cref="PumpVideo"/>. There is no poll timer, no
/// transport ack wait and no seek suppression window: the first frame is on screen one host frame after the engine
/// presents it, a seek publishes its target at once and its landed position on the Seeked event, and position is a
/// timestamped native sample extrapolated by elapsed·rate exactly as the clear path does.</para>
/// <para><b>One timer, one shot.</b> A source that never reaches CANPLAY within <see cref="StartBudget"/> is reported as
/// a typed failure (Drm while the license is pending or the topology is being built, Network while the store is still
/// empty). It is armed once at connect and disarmed by the first frame; it is a deadline, not a poll.</para>
/// </summary>
public sealed class ProtectedMediaSession : IMediaSession, IVideoSurfaceSession, IVideoPumpSource
{
    private readonly IProtectedVideoPlayer _player;
    private readonly ProtectedVideoRequest _request;
    private readonly MediaOpenOptions _opts;
    private readonly MediaLocus _locus;
    private readonly TimeSpan _startBudget;

    private MediaSignalSink? _sink;
    private bool _disposed;
    private bool _started;
    private bool _playRequested;
    private double _rate = 1.0;

    private SizeI _naturalSize = SizeI.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _commandsPublished;
    private bool _errorPublished;
    private double _volume = 1.0;
    private bool _muted;
    private long _lastFirstFrameEpoch;
    private bool _settledPlayIntent;

    // Seek (UI thread): the target is published immediately; the landed value replaces it on the Seeked event.
    private bool _seekPublished;
    private long _seekTargetMs;

    // The CANPLAY deadline (a single one-shot timer; fires one pump).
    private Timer? _startDeadline;
    private long _startTicks;
    private int _deadlinePassed;

    // ABR.
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

    // Throughput sample accumulation: one pump slice of a 2 Mbps stream can sit under the estimator's 64 KB noise floor,
    // so slices are accumulated into one sample big enough to mean something. Idle slices are never charged.
    private long _pendingSampleBytes;
    private long _pendingSampleMs;
    private long _lastSampleBytes;
    private long _lastSampleMs;
    private double _lastSampleKbps;

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
        _startBudget = StartBudget(opts.Buffering);
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
        _player.PumpRequested += OnPlayerPumpRequested;
    }

    /// <summary>The underlying protected player (the prepared/attached source).</summary>
    public IProtectedVideoPlayer Player => _player;

    /// <summary>How long a source may take to reach CANPLAY before it is reported as failed: the buffer policy's
    /// initial-playback target × 10, never less than 10 s. Pure (the engine's session tests pin it).</summary>
    public static TimeSpan StartBudget(BufferPolicy? policy)
    {
        TimeSpan initial = policy?.InitialPlayback ?? TimeSpan.FromSeconds(1);
        TimeSpan budget = TimeSpan.FromTicks(initial.Ticks * 10);
        return budget < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : budget;
    }

    /// <summary>
    /// The buffering detail for a published <paramref name="state"/> — the SAME rule the clear session applies
    /// (<c>MfMediaSession.PublishBuffering</c>): <see cref="PlaybackState.Opening"/>, <see cref="PlaybackState.Buffering"/>
    /// and <see cref="PlaybackState.Stalled"/> are all buffering, with the reason Seeking while a seek is in flight,
    /// Rebuffering once a frame has been presented, Initial before. Every other state is <see cref="BufferingInfo.None"/>.
    /// Publishing None for Stalled or Opening (what this once did) wiped the reason the player core had just derived, so
    /// a mid-playback rebuffer read as "not buffering" on the protected path only. The progress is the forward store
    /// against the policy's target for that reason. Pure.
    /// </summary>
    public static BufferingInfo BufferingFor(PlaybackState state, bool seeking, bool framePresented, long forwardBufferedMs,
                                             BufferPolicy policy)
    {
        if (state is not (PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled)) return BufferingInfo.None;
        BufferingReason reason = seeking ? BufferingReason.Seeking
            : framePresented ? BufferingReason.Rebuffering : BufferingReason.Initial;
        TimeSpan target = reason == BufferingReason.Initial ? policy.InitialPlayback : policy.ResumePlayback;
        TimeSpan ahead = TimeSpan.FromMilliseconds(Math.Max(0, forwardBufferedMs));
        double percent = target > TimeSpan.Zero ? Math.Clamp(ahead.TotalMilliseconds / target.TotalMilliseconds, 0.0, 1.0) : -1;
        return new BufferingInfo(reason, percent, ahead, target, target > TimeSpan.Zero && ahead >= target);
    }

    /// <summary>Which error a start that ran out of <see cref="StartBudget"/> is: a license still pending or a topology
    /// that never produced metadata is a protected-path (Drm) failure; a store that never received media is Network.</summary>
    public static MediaErrorCategory StartFailureCategory(ProtectedVideoPhase phase)
        => phase == ProtectedVideoPhase.Buffering ? MediaErrorCategory.Network : MediaErrorCategory.Drm;

    /// <summary>The position to publish: the native sample, extrapolated by the time since it was taken × rate while the
    /// clock runs (the clear path's rule — a pump between two native samples reports a moving playhead, not a
    /// stair-step). Pure.</summary>
    public static long ExtrapolatePositionMs(long sampleMs, long sampleTimestamp, bool playing, double rate, long nowTimestamp)
    {
        if (!playing || sampleTimestamp == 0 || nowTimestamp <= sampleTimestamp) return Math.Max(0, sampleMs);
        double elapsedMs = (nowTimestamp - sampleTimestamp) * 1000.0 / Stopwatch.Frequency;
        return Math.Max(0, sampleMs + (long)(elapsedMs * rate));
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        sink.PlayRequested(!_opts.StartPaused);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        // The carried start position is published at once: the seek bar never shows 0:00 for a source opening at 1:23.
        if (_request.StartPosition > TimeSpan.Zero) sink.Position(_request.StartPosition);
        PublishCatalog(sink);
        StartOnce();
        if (!_qualitySelection.IsAuto && _qualitySelection.VariantId is { } initialPin)
            RequestRepresentation(initialPin);
        RequestPump();
    }

    private void OnPlayerPumpRequested() => RequestPump();

    private void RequestPump()
    {
        if (_disposed) return;
        try { PumpRequested?.Invoke(); } catch { }
    }

    private void StartOnce()
    {
        if (_started) return;
        _started = true;
        _startTicks = Environment.TickCount64;
        _lastGrowthTicks = _startTicks;
        _startDeadline = new Timer(static s => ((ProtectedMediaSession)s!).OnStartDeadline(), this,
            (long)_startBudget.TotalMilliseconds, Timeout.Infinite);
        _player.Start(_request with { StartPaused = !_playRequested });
    }

    private void OnStartDeadline()
    {
        Volatile.Write(ref _deadlinePassed, 1);
        RequestPump();
    }

    private void DisarmStartDeadline()
    {
        Timer? t = Interlocked.Exchange(ref _startDeadline, null);
        t?.Dispose();
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _player.HasSurface && !_naturalSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _naturalSize, IsHdr: false)
            : VideoDelivery.None;

    // ── transport (idempotent; accepted synchronously; the pump realizes state from events) ────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = true;
        _settledPlayIntent = false;
        _sink?.PlayRequested(true);
        if (!_started) StartOnce();
        return _player.PlayAsync();
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _settledPlayIntent = false;
        _sink?.PlayRequested(false);
        return _player.PauseAsync();
    }

    /// <inheritdoc/>
    public void PublishSeekIntent(TimeSpan target)
    {
        if (_disposed || _sink is null) return;
        long ms = ClampToDuration(target);
        _seekTargetMs = ms;
        _seekPublished = true;
        // The target moves the transport UI NOW, on the calling thread. The Seeked event (not a timer, not a tolerance
        // window) replaces it with the landed position.
        _sink.Position(TimeSpan.FromMilliseconds(ms));
        Publish(_sink, PlaybackState.Buffering);
        _sink.Buffering(new BufferingInfo(BufferingReason.Seeking, -1, TimeSpan.Zero,
            (_opts.Buffering ?? BufferPolicy.Vod).ResumePlayback, false));
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => SeekAsync(to, mode, keyframeMs: -1);

    /// <summary>Seek with the host planner's keyframe answer (<paramref name="keyframeMs"/>, -1 = native decides), so the
    /// native side does not repeat the search. Completes at once; Seeking/Seeked arrive as events.</summary>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode, long keyframeMs)
    {
        if (_disposed) return ValueTask.CompletedTask;
        long ms = ClampToDuration(to);
        PublishSeekIntent(to);
        // A seek does not change the network: the throughput history survives; only the ladder's vote/probe is stale.
        _abr?.ResetForSeek();
        return _player.SeekAsync(ms, mode, keyframeMs);
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
        _rate = rate > 0 ? rate : 1.0;
        _player.SetRate((float)_rate);
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
            _pendingRepresentationTicks = Environment.TickCount64;
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
        RequestPump();
    }

    /// <summary>The floor the laid-out viewport height is clamped to before it becomes an ABR cap. Real video surfaces
    /// are small (191 DIP docked, 202 DIP pop-out, 135 DIP at the pop-out minimum) and every one is below every rung a
    /// manifest offers, so the raw height would filter the whole ladder away and Auto would collapse to the bottom rung
    /// on any bandwidth. A viewport is a hint about what is worth downloading, not a licence to starve the ladder.</summary>
    private const int MinViewportCapHeight = 720;

    /// <inheritdoc/>
    public void SetAdaptiveViewportHeight(int height)
    {
        if (_abr is null) return;
        // Zero/negative is "not laid out yet"; a height during an in-flight representation switch describes a surface
        // mid-replacement — neither is a real viewport.
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

    // ── the UI-thread pump ─────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        MediaSignalSink sink = _sink;

        _player.Pump(binding);
        ProtectedVideoState pv = _player.State.Value;
        UpdateAdaptiveState(sink);

        // 1. Terminal error → typed MediaError, published once. Never a silent drop.
        if (pv == ProtectedVideoState.Error)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                DisarmStartDeadline();
                sink.Error(new MediaError(MediaErrorCategory.Drm,
                    _player.Error.Value ?? "Protected playback failed (CDM/license).", null, _locus, MediaRecovery.NeedsLicense));
                Publish(sink, PlaybackState.Failed);
            }
            return;
        }

        // 2. First frame: the deadline is met.
        long firstFrameEpoch = _player.FirstFrameEpoch;
        if (firstFrameEpoch != _lastFirstFrameEpoch)
        {
            _lastFirstFrameEpoch = firstFrameEpoch;
            DisarmStartDeadline();
        }

        // 2b. The CANPLAY deadline passed with no first frame: a typed failure, categorised by where the switch stuck.
        if (Volatile.Read(ref _deadlinePassed) != 0 && _lastFirstFrameEpoch == 0 && !_errorPublished)
        {
            _errorPublished = true;
            ProtectedVideoPhase stuck = _player.Phase;
            MediaErrorCategory category = StartFailureCategory(stuck);
            string message = $"Protected video did not start within {_startBudget.TotalSeconds:0.#}s (stuck at {stuck}). " +
                             (category == MediaErrorCategory.Network
                                 ? "No media arrived from the CDN."
                                 : "The license or the protected decoder never became ready.");
            _player.LogDiagnostic($"start.timeout phase={stuck} budgetMs={(long)_startBudget.TotalMilliseconds} " +
                                  $"sinceOpenMs={Environment.TickCount64 - _startTicks}");
            sink.Error(new MediaError(category, message, null, _locus,
                category == MediaErrorCategory.Network ? MediaRecovery.NeedsNetwork : MediaRecovery.NeedsLicense));
            Publish(sink, PlaybackState.Failed);
            return;
        }

        // 3. Natural size / duration / commands.
        Size2 ns = _player.NaturalSize.Value;
        if (ns.Width > 0 && (_naturalSize.Width != (int)ns.Width || _naturalSize.Height != (int)ns.Height))
        {
            _naturalSize = new SizeI((int)ns.Width, (int)ns.Height);
            SizeI displaySize = _activeQuality?.Resolution is { IsEmpty: false } selected ? selected : _naturalSize;
            sink.NaturalSize(displaySize);
            if (!_commandsPublished)
            {
                _commandsPublished = true;
                var commands = MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate;
                if (_player.SupportsAdaptiveSelection && _videoTrack is { Representations.Count: > 1 })
                    commands |= MediaCommandFlags.SelectVideoQuality;
                sink.Commands(commands);
            }
        }
        long durMs = _player.DurationMs.Value;
        if (durMs > 0 && (long)_duration.TotalMilliseconds != durMs)
        {
            _duration = TimeSpan.FromMilliseconds(durMs);
            sink.Duration(_duration);
        }

        // 4. The composited surface (already bound by the player's pump): size the STREAM to what the destination can
        //    show — the same rule as the clear path, so a 4K rung in a 640-px card allocates 640-px buffers — and place it.
        if (binding.IsValid && _player.HasSurface)
        {
            SizeI content = VideoStreamSizing.ContentSizeFor(_naturalSize, videoRect, scale);
            _player.SetStreamSize(content);
            binding.SetContentSize(content);
            binding.Place(videoRect);
            binding.SetVisible(true);
            sink.SurfaceGeometry(new VideoSurfaceGeometry(_naturalSize, content, videoRect, scale <= 0f ? 1f : scale)
                { Token = binding.Token });
        }

        // 5. State + position. A seek holds the published target until the Seeked event, then the landed position
        //    takes over — no tolerance window, no timeout.
        bool seeking = _player.IsSeeking;
        if (_seekPublished && !seeking)
        {
            _seekPublished = false;
            sink.SettleTransport();
            long landed = _player.LastSeekLandedMs;
            _player.LogDiagnostic($"seek.landed target={_seekTargetMs} landed={(landed >= 0 ? landed : _player.PositionMs.Value)}");
        }

        PlaybackState state = seeking ? PlaybackState.Buffering : MapState(pv, _playRequested);
        Publish(sink, state);
        sink.Buffering(BufferingFor(state, seeking, _lastFirstFrameEpoch != 0, _player.ForwardBufferedMs,
            _opts.Buffering ?? BufferPolicy.Vod));

        if (!_settledPlayIntent && ((_playRequested && pv == ProtectedVideoState.Playing)
                                    || (!_playRequested && pv == ProtectedVideoState.Paused)))
        {
            _settledPlayIntent = true;
            sink.SettleTransport();
        }

        if (!seeking)
        {
            long pos = ExtrapolatePositionMs(_player.PositionMs.Value, _player.PositionQpc,
                pv == ProtectedVideoState.Playing, _rate, Stopwatch.GetTimestamp());
            if (_lastFirstFrameEpoch != 0 || pos > 0) sink.Position(TimeSpan.FromMilliseconds(pos));
        }
    }

    private void PublishCatalog(MediaSignalSink sink)
    {
        sink.ResetTracks();
        if (_request.Catalog is null) return;
        for (int i = 0; i < _request.Catalog.Tracks.Count; i++)
        {
            ProtectedTrackDescriptor track = _request.Catalog.Tracks[i];
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
            // The backend rebased its counters (a new session, or a representation switch) — never sample across that.
            _lastBytesDownloaded = bytes;
            _lastDownloadElapsedMs = downloadMs;
            _pendingSampleBytes = 0;
            _pendingSampleMs = 0;
            _lastGrowthTicks = now;
        }
        else if (bytes > _lastBytesDownloaded)
        {
            // Prefer the backend's transfer-only clock when it moves; otherwise charge the wall time since the last slice
            // that ACTUALLY moved bytes (idle gaps between segment fetches are never charged).
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
                _lastSampleKbps = _pendingSampleBytes * 8.0 / _pendingSampleMs;
                _pendingSampleBytes = 0;
                _pendingSampleMs = 0;
            }
        }
        else
        {
            _lastGrowthTicks = now;
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
            // A profile with no declared resolution must not publish an empty natural size mid-playback: that flips the
            // element into audio-only and both the hole and the surface disappear.
            if (!active.Quality.Resolution.IsEmpty) sink.NaturalSize(active.Quality.Resolution);
            else _player.LogDiagnostic($"representation '{activeId}' has no declared resolution — keeping the previous natural size");
        }

        if (_pendingRepresentationId is not null && now - _pendingRepresentationTicks > RepresentationPendingTimeoutMs)
        {
            _player.LogDiagnostic($"representation '{_pendingRepresentationId}' never became active within " +
                                  $"{RepresentationPendingTimeoutMs}ms — releasing the ABR gate");
            _pendingRepresentationId = null;
        }

        if (!_qualitySelection.IsAuto || now - _lastAbrTicks < 1_000) return;
        _lastAbrTicks = now;
        long bufferedMs = Math.Max(0, _player.ForwardBufferedMs);
        int chosen = _abr.Choose(_qualityVariants, TimeSpan.FromMilliseconds(bufferedMs));
        QualityVariant pick = _qualityVariants[Math.Clamp(chosen, 0, _qualityVariants.Length - 1)];

        if (!string.Equals(pick.Id, _activeQuality?.Id, StringComparison.Ordinal)
            && !string.Equals(pick.Id, _pendingRepresentationId, StringComparison.Ordinal))
        {
            // Always-on, and only on an actual rung change: the sample that fed the estimate, both EWMAs, the buffer,
            // the cap and WHY — the one line that explains an "Auto · 240p" from a field log with no repro.
            _player.LogDiagnostic(
                $"abr sample={_lastSampleBytes}B/{_lastSampleMs}ms={_lastSampleKbps:F0}kbps " +
                $"fast={_abr.FastKbps:F0} slow={_abr.SlowKbps:F0} n={_abr.ThroughputSamples} " +
                $"buffer={bufferedMs}ms cap={(_abr.MaxHeight == int.MaxValue ? "none" : _abr.MaxHeight.ToString())} " +
                $"active={_activeQuality?.Id ?? "-"} -> idx={chosen} id={pick.Id} " +
                $"{pick.Resolution.Width}x{pick.Resolution.Height}@{pick.Bitrate} why={_abr.LastDecisionReason}");
            RequestRepresentation(pick.Id);
        }
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

    private static ProtectedTrackDescriptor? FindDefaultTrack(ProtectedAdaptiveCatalog? catalog, MediaTrackKind kind)
    {
        if (catalog is null) return null;
        ProtectedTrackDescriptor? first = null;
        for (int i = 0; i < catalog.Tracks.Count; i++)
        {
            ProtectedTrackDescriptor track = catalog.Tracks[i];
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

    /// <summary>Map the protected lifecycle onto the player's transport state. Pure.
    /// <para><see cref="ProtectedVideoState.Paused"/> maps to <see cref="PlaybackState.Paused"/> — including a source
    /// opened paused, which the native runtime reports as Paused the moment its first frame is up. Paused while a play
    /// is requested reads <see cref="PlaybackState.Buffering"/> (the intent is accepted, the engine is not advancing yet).
    /// Loading and Licensed are <see cref="PlaybackState.Opening"/>; a mid-playback rebuffer is
    /// <see cref="PlaybackState.Stalled"/>.</para></summary>
    public static PlaybackState MapState(ProtectedVideoState s, bool playRequested) => s switch
    {
        ProtectedVideoState.Idle => PlaybackState.Opening,
        ProtectedVideoState.Loading or ProtectedVideoState.Licensed => PlaybackState.Opening,
        ProtectedVideoState.Buffering => PlaybackState.Stalled,
        ProtectedVideoState.Playing => PlaybackState.Playing,
        ProtectedVideoState.Paused => playRequested ? PlaybackState.Buffering : PlaybackState.Paused,
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
        DisarmStartDeadline();
        PumpRequested = null;
        _player.PumpRequested -= OnPlayerPumpRequested;
        // Detach + destroy are posted native work items (non-blocking); the runtime and its engine stay warm.
        try { _player.Stop(); } catch { }
        try { _player.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
