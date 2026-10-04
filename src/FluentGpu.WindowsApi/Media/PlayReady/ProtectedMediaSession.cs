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
/// contract the clear session uses) and does every signal write in <see cref="PumpState"/>, which needs no element (F132:
/// the owning <c>MediaPlayer</c> runs it); only the surface bind, the stream sizing and the placement report wait for an
/// element, in <see cref="PumpGeometry"/>. There is no poll timer, no
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
    private int _started;
    private bool _playRequested;
    private double _rate = 1.0;

    private SizeI _naturalSize = SizeI.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _commandsPublished;
    private bool _errorPublished;
    // The native frame counters last handed to sink.Statistics (F066); -1 = nothing published yet.
    private long _statsRendered = -1, _statsDropped = -1;
    private double _volume = 1.0;
    private bool _muted;
    private long _lastFirstFrameEpoch;
    private VideoSurfaceId _publishedSurface;   // last value handed to sink.VideoSurface (see pump step 2a)
    private bool _settledPlayIntent;

    // Seek (UI thread): the target is published immediately; the landed value replaces it on the Seeked event.
    private bool _seekPublished;
    private long _seekTargetMs;

    // Decides the stream size (a bucket of the natural frame, settled, and echoed by the native snapshot before the
    // compositor is told) — shared with the clear session. _sizeRetry re-pumps once a settle window or an echo wait ends.
    private readonly VideoStreamSizeGate _sizeGate = new();
    private Timer? _sizeRetry;
    // The clock the size gate reads; a test replaces it to step time.
    internal Func<long> ClockMs { get; set; } = static () => Environment.TickCount64;

    // The CANPLAY deadline (a single one-shot timer; fires one pump).
    private Timer? _startDeadline;
    private long _startTicks;
    private int _deadlinePassed;

    // ABR.
    private readonly ProtectedTrackDescriptor? _videoTrack;
    private readonly QualityVariant[] _qualityVariants = Array.Empty<QualityVariant>();
    private readonly AdaptiveBitrateController? _abr;
    // The viewport cap THIS session last committed to the shared controller (int.MaxValue = none yet), plus the pending
    // LOWER cap being timed. The policy cap lives on the controller only; the session never reads a cap back as policy.
    private int _viewportMaxHeight = int.MaxValue;
    private int _viewportLowerCandidate;
    private long _viewportLowerSinceTicks;
    private QualitySelection _qualitySelection = QualitySelection.Auto;
    private QualityVariant? _activeQuality;               // the rung ON SCREEN (published to the player)
    private string? _downloadingQualityId;                // the rung the downloader is on: the ABR's baseline
    private string? _pendingRepresentationId;
    private long _pendingRepresentationTicks;
    // Switch pacing (AbrSwitchGate): the minimum interval between ABR-initiated switches, and the rule that a forced probe
    // is judged only on samples from its own rung. _probeRungId is the rung that probe stepped to.
    private readonly AbrSwitchGate _gate = new();
    private string? _probeRungId;
    // A viewport RAISE (entering fullscreen) is the one upswitch that should show soon: the next upswitch inside the window
    // keeps only ViewportUpswitchRetainMs of the old representation's buffer instead of appending after all of it.
    private bool _viewportRaised;
    private long _viewportRaiseTicks;
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
            // The controller is shared across sources: keep its throughput history and policy cap, drop the previous
            // source's ladder position, probe/climb state and viewport cap.
            _abr.ResetForNewSource();
            _qualitySelection = _abr.Selection;
            _activeQuality = FindInitialQuality(_videoTrack, request.InitUrl);
            _downloadingQualityId = _activeQuality?.Id;
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

    /// <summary>The longest, in wall-clock ms, a native position sample is extrapolated forward: about two TIMEUPDATE
    /// intervals (the runtime samples the engine clock at least every ~200 ms while it runs). A clock that has stopped
    /// without the state saying so (a stall whose WAITING never came, or a TIMEUPDATE that went quiet) therefore drifts
    /// the published position by at most this much, instead of running on forever from a stale sample.</summary>
    public const long MaxExtrapolationMs = 500;

    /// <summary>The position to publish: the native sample, extrapolated by the time since it was taken × rate while the
    /// clock runs (the clear path's rule — a pump between two native samples reports a moving playhead, not a
    /// stair-step), for at most <see cref="MaxExtrapolationMs"/> of wall-clock time past the sample. Pure.</summary>
    public static long ExtrapolatePositionMs(long sampleMs, long sampleTimestamp, bool playing, double rate, long nowTimestamp)
    {
        if (!playing || sampleTimestamp == 0 || nowTimestamp <= sampleTimestamp) return Math.Max(0, sampleMs);
        double elapsedMs = Math.Min((nowTimestamp - sampleTimestamp) * 1000.0 / Stopwatch.Frequency, MaxExtrapolationMs);
        return Math.Max(0, sampleMs + (long)(elapsedMs * rate));
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        // The play intent is the session's own (an early PlayAsync may have raised it before the sink connected); it equals
        // !StartPaused until then.
        sink.PlayRequested(_playRequested);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        // The carried start position is published at once: the seek bar never shows 0:00 for a source opening at 1:23.
        if (_request.StartPosition > TimeSpan.Zero) sink.Position(_request.StartPosition);
        PublishCatalog(sink);
        StartOnce();
        if (!_qualitySelection.IsAuto && _qualitySelection.VariantId is { } initialPin)
            RequestRepresentation(initialPin, PinRetainMs, Environment.TickCount64);
        RequestPump();
    }

    private void OnPlayerPumpRequested() => RequestPump();

    private void RequestPump()
    {
        if (_disposed) return;
        try { PumpRequested?.Invoke(); } catch { }
    }

    /// <summary>
    /// Begins the native open (prepare + attach) WITHOUT a sink: <c>MediaPlayer</c> calls this on the opening thread the
    /// moment <c>OpenAsync</c> returns the session, so the segment fetch and licence work no longer wait for the UI hop
    /// that connects the signals. Idempotent and safe from any thread (<see cref="ConnectSignals"/> and
    /// <see cref="PlayAsync"/> call the same once-only start). A session disposed before the call never starts.
    /// <para>Off-UI-thread safety: the start only arms the deadline timer and calls <c>_player.Start</c> (native session
    /// calls plus pump requests, which the session may always raise from a worker thread). It touches no sink and no UI
    /// state, and the deferred-start continuation already runs the same <c>Attach</c> from a pool thread.</para>
    /// </summary>
    public void Start() => StartOnce();

    private void StartOnce()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
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

    // Asks for one more pump after `ms` (a settle window or an echo wait is pending): the native runtime raises no event for
    // an applied stream size, and a deferred size decision must not wait for an unrelated one. Any thread.
    private void ArmSizeRetry(int ms)
    {
        if (ms <= 0 || _disposed) return;
        _sizeRetry ??= new Timer(static s => ((ProtectedMediaSession)s!).RequestPump(), this, Timeout.Infinite, Timeout.Infinite);
        try { _sizeRetry.Change(ms, Timeout.Infinite); }
        catch (ObjectDisposedException) { }   // disposed from another thread between the check and here
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
        StartOnce();
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
            _gate.EndProbe();   // a pin ends any probe: the user's rung is not a verdict on the link
            _gate.NoteSwitch(_pendingRepresentationTicks);
            try
            {
                await _player.SelectVideoRepresentationAsync(id, PinRetainMs).ConfigureAwait(false);
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

    /// <summary>How long a LOWER viewport cap must hold unchanged before it is committed. A fullscreen exit animates
    /// through many heights; a cap that follows each one samples a surface mid-resize. Raising is never delayed.</summary>
    private const long ViewportLowerSettleMs = 1_500;

    /// <inheritdoc/>
    public void SetAdaptiveViewportHeight(int height) => ApplyViewportHeight(height, Environment.TickCount64);

    /// <summary>The viewport-cap policy behind <see cref="SetAdaptiveViewportHeight"/>, with the clock passed in so the
    /// settle is testable. The height is floored at <see cref="MinViewportCapHeight"/> and quantised UP to a ladder height
    /// (so 1012 px and 1080 px fullscreen are the same cap). The first cap and every RAISE commit at once; a LOWER cap
    /// commits only after it has been the requested cap for <see cref="ViewportLowerSettleMs"/>.</summary>
    internal void ApplyViewportHeight(int height, long nowTicks)
    {
        AdaptiveBitrateController? abr = _abr;
        // Zero/negative is "not laid out yet"; a height during an in-flight representation switch describes a surface
        // mid-replacement — neither is a real viewport. A pending switch also restarts any lower-cap timer, so the
        // switch itself never counts towards "stable".
        if (abr is null || _disposed || height <= 0) return;
        if (_pendingRepresentationId is not null) { _viewportLowerCandidate = 0; return; }
        int cap = QuantiseViewportCap(Math.Max(height, MinViewportCapHeight));
        if (_viewportMaxHeight == int.MaxValue || cap >= _viewportMaxHeight)
        {
            CommitViewportCap(abr, cap, nowTicks);
            return;
        }
        if (cap != _viewportLowerCandidate)
        {
            _viewportLowerCandidate = cap;
            _viewportLowerSinceTicks = nowTicks;
            return;
        }
        if (nowTicks - _viewportLowerSinceTicks >= ViewportLowerSettleMs) CommitViewportCap(abr, cap, nowTicks);
    }

    private void CommitViewportCap(AdaptiveBitrateController abr, int cap, long nowTicks)
    {
        if (_viewportMaxHeight != int.MaxValue && cap > _viewportMaxHeight)
        {
            _viewportRaised = true;
            _viewportRaiseTicks = nowTicks;
        }
        _viewportMaxHeight = cap;
        _viewportLowerCandidate = 0;
        abr.ViewportMaxHeight = cap;
    }

    /// <summary>The smallest ladder height at or above <paramref name="height"/>; the tallest rung when the surface is
    /// taller than every rung; the raw height when no rung declares a resolution.</summary>
    private int QuantiseViewportCap(int height)
    {
        int atOrAbove = int.MaxValue, tallest = 0;
        for (int i = 0; i < _qualityVariants.Length; i++)
        {
            int rung = _qualityVariants[i].Resolution.Height;
            if (rung > tallest) tallest = rung;
            if (rung >= height && rung < atOrAbove) atOrAbove = rung;
        }
        if (atOrAbove != int.MaxValue) return atOrAbove;
        return tallest > 0 ? tallest : height;
    }

    /// <summary>
    /// The typed error for a protected session that ended in <see cref="ProtectedVideoState.Error"/>. A failure a fresh runtime
    /// cures (the runtime was replaced after a device removal/reset or a hardware-DRM context reset, or never came up) is
    /// <see cref="MediaRecovery.Retryable"/> in a NON-DRM category with the HRESULT as <see cref="MediaError.UnderlyingCode"/>:
    /// the owner reopens the source in place instead of telling the user their license failed. Anything else keeps the
    /// historical <see cref="MediaErrorCategory.Drm"/> + <see cref="MediaRecovery.NeedsLicense"/>. Pure.
    /// </summary>
    internal static MediaError ProtectedFailure(string? message, int hr, bool needsRuntimeRebuild, MediaLocus? locus)
    {
        if (needsRuntimeRebuild || ProtectedRuntimeFaults.IsRuntimeReset(hr))
            return new MediaError(MediaErrorCategory.Output,
                message ?? $"The protected video pipeline was reset (0x{unchecked((uint)hr):X8}).",
                hr != 0 ? hr : null, locus, MediaRecovery.Retryable);
        return new MediaError(MediaErrorCategory.Drm,
            message ?? "Protected playback failed (CDM/license).", null, locus, MediaRecovery.NeedsLicense);
    }

    // F066: the native runtime polls the engine's FRAMES_RENDERED / FRAMES_DROPPED at its own bounded cadence, so the counters the
    // snapshot carries change at most a couple of times a second; the sink is written only when they do. Published before the error
    // gate so the counters of a source that failed (or was judged hung) stay on the signal the diagnostics read.
    private void PublishStatistics(MediaSignalSink sink)
    {
        long rendered = _player.FramesRendered, dropped = _player.FramesDropped;
        if (rendered == _statsRendered && dropped == _statsDropped) return;
        if (_statsRendered < 0 && rendered == 0 && dropped == 0) return;   // nothing to say yet: keep Empty
        _statsRendered = rendered;
        _statsDropped = dropped;
        sink.Statistics(StatisticsFrom(rendered, dropped));
    }

    internal static PlaybackStatistics StatisticsFrom(long framesRendered, long framesDropped)
        => new(BytesDownloaded: 0, FramesDecoded: framesRendered + framesDropped, FramesDropped: framesDropped,
               AudioUnderruns: 0, EstimatedThroughputKbps: 0, VideoBitrateKbps: 0, AudioBitrateKbps: 0,
               StartupTime: TimeSpan.Zero, RebufferTime: TimeSpan.Zero, RebufferCount: 0, FramesRendered: framesRendered);

    /// <inheritdoc/>
    public void SetAdaptiveMaxHeight(int height)
    {
        if (_abr is null) return;
        // POLICY only: the viewport cap is a separate controller input and the effective cap is min(policy, viewport).
        _abr.PolicyMaxHeight = height > 0 ? height : int.MaxValue;
    }

    // ── the UI-thread pump ─────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        PumpState();
        PumpGeometry(binding, videoRect, scale);
    }

    /// <inheritdoc/>
    public void PumpState()
    {
        if (_disposed || _sink is null) return;
        MediaSignalSink sink = _sink;

        // The STATE half (F132): one native snapshot, then everything this session publishes without a surface — the error,
        // the first-frame deadline, the VideoSurface readiness, natural size, duration, state, buffering and position. No
        // element is needed and no binding is touched, so MediaPlayer runs it on every pump request.
        _player.Pump();
        ProtectedVideoState pv = _player.State.Value;
        UpdateAdaptiveState(sink, Environment.TickCount64);
        PublishStatistics(sink);

        // 1. Terminal error → typed MediaError, published once. Never a silent drop.
        if (pv == ProtectedVideoState.Error)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                DisarmStartDeadline();
                sink.Error(ProtectedFailure(_player.Error.Value, _player.ErrorHr, _player.ErrorNeedsRuntimeRebuild, _locus));
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
        // 2a. The player's VideoSurface signal — what MediaPlayerElement's poster/hole gate reads (framePresented):
        //     non-None once THIS attach has presented a frame and still has a surface, None before that and again the
        //     moment the surface is detached. No real backend wrote this signal until 2026-09-22 (only the headless
        //     scripted player did), so the poster never dropped and the video hole was never punched: every placement
        //     showed the letterbox fill — black — over a perfectly good picture. Value-gated here so a steady pump
        //     publishes nothing.
        bool presenting = _player.HasFirstFrame && _player.HasSurface;
        var surfaceNow = presenting ? new VideoSurfaceId(1) : default;
        if (surfaceNow != _publishedSurface)
        {
            _publishedSurface = surfaceNow;
            sink.VideoSurface(surfaceNow);
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

    /// <inheritdoc/>
    public void PumpGeometry(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null || !binding.IsValid) return;
        MediaSignalSink sink = _sink;

        // The SURFACE half (F132): hand the handle the last state pump saw to this element's binding, then size and place it.
        // It reads what the state pump published (state, natural size, surface readiness) and writes none of it, so a pump
        // with no element and an element with no pump of its own never disagree. A terminal error ends the surface work.
        _player.Bind(binding);
        ProtectedVideoState pv = _player.State.Value;
        if (pv == ProtectedVideoState.Error) return;
        bool presenting = _player.HasFirstFrame && _player.HasSurface;

        // The composited surface (just bound above): size the STREAM to what the destination can show — the same rule as the
        // clear path, so a 4K rung in a 640-px card allocates a third-size buffer — and place it.
        // It is SHOWN only while presenting (this attach's first frame and a live surface): MF's swap chain holds the
        // previous source's last frame until the new one decodes, and a detached slot would show another session's.
        // The stream is a BUCKET of the natural frame (never the raw destination), requested once the destination has held
        // still, and the compositor keeps the previous content size until the native snapshot echoes the new one as
        // applied. A rect that is not laid out sizes and places nothing (F051): the slot just stays hidden.
        if (_player.HasSurface)
        {
            if (VideoStreamSizing.IsLaidOut(videoRect))
            {
                VideoStreamStep step = _sizeGate.Step(_naturalSize, videoRect, scale, _player.AppliedStreamSize,
                    pv == ProtectedVideoState.Playing, ClockMs());
                if (!step.Request.IsEmpty) _player.SetStreamSize(step.Request, binding.Token, binding.HostOrdinal);
                ArmSizeRetry(step.RetryInMs);
                SizeI content = step.Content;
                binding.SetContentSize(content);
                binding.Place(videoRect);
                PlaceOutputProtection(binding, videoRect, scale);
                binding.SetVisible(presenting);   // belt-and-braces: the element ANDs the same readiness (VideoSurface) into its final write
                sink.SurfaceGeometry(new VideoSurfaceGeometry(_naturalSize, content, videoRect, scale <= 0f ? 1f : scale)
                    { Token = binding.Token });
            }
            else
            {
                binding.SetVisible(false);
            }
        }
        else
        {
            binding.SetVisible(false);   // detached (or not produced yet): never leave a stale slot showing
        }
    }

    /// <summary>Keep the runtime's hidden OPM window over the video (F264): the placed rect in device pixels of the presenting
    /// window's client area. An inert binding, or a registry that was never told its window (headless), places nothing.</summary>
    private void PlaceOutputProtection(in VideoBinding binding, RectF videoRect, float scale)
    {
        nuint host = binding.WindowHandle;
        if (host == 0) return;
        (int left, int top, int right, int bottom) = OutputProtectionRect(videoRect, scale);
        _player.PlaceOutputProtectionWindow(host, left, top, right, bottom);
    }

    /// <summary>The video rect (DIP) in device pixels, pixel rule R: X, Y, Right and Bottom each rounded independently,
    /// midpoints away from zero, so two neighbours never differ from the DComp rect by more than the rounding itself. Pure.</summary>
    internal static (int Left, int Top, int Right, int Bottom) OutputProtectionRect(RectF videoRect, float scale)
    {
        float s = scale <= 0f ? 1f : scale;
        return ((int)MathF.Round(videoRect.X * s, MidpointRounding.AwayFromZero),
                (int)MathF.Round(videoRect.Y * s, MidpointRounding.AwayFromZero),
                (int)MathF.Round(videoRect.Right * s, MidpointRounding.AwayFromZero),
                (int)MathF.Round(videoRect.Bottom * s, MidpointRounding.AwayFromZero));
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

    /// <summary>One ABR tick at clock <paramref name="now"/> (ms): fold the throughput sample, follow the on-screen and
    /// downloading rungs, and — at most once a second, never while a switch is pending — decide. The clock is passed in so
    /// the gate's intervals are testable.</summary>
    internal void UpdateAdaptiveState(MediaSignalSink sink, long now)
    {
        if (_videoTrack is null || _abr is null || _qualityVariants.Length == 0) return;
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

        // Two representation ids, because a switch no longer takes effect at once. ON SCREEN (active) is what the quality
        // label and the natural size follow; DOWNLOADING is what the ABR judges against and what clears the pending gate
        // (Media3 compares with the last QUEUED chunk's format). A switch appended behind 40 s of old video is downloading
        // within a second and on screen 40 s later, and neither the pending timeout nor the controller may wait for the
        // second.
        string? activeId = _player.ActiveVideoRepresentationId;
        string? downloadingId = _player.DownloadingVideoRepresentationId ?? activeId;
        if (activeId is not null && !string.Equals(activeId, _activeQuality?.Id, StringComparison.Ordinal)
            && FindRepresentation(_videoTrack, activeId) is { } active)
        {
            _activeQuality = active.Quality;
            if (string.Equals(_pendingRepresentationId, activeId, StringComparison.Ordinal)) _pendingRepresentationId = null;
            sink.QualitySelection(_qualitySelection, _activeQuality);
            // A profile with no declared resolution must not publish an empty natural size mid-playback: that flips the
            // element into audio-only and both the hole and the surface disappear.
            if (!active.Quality.Resolution.IsEmpty) sink.NaturalSize(active.Quality.Resolution);
            else _player.LogDiagnostic($"representation '{activeId}' has no declared resolution — keeping the previous natural size");
        }
        int downloadingIndex = IndexOfVariant(downloadingId);
        if (downloadingIndex >= 0 && !string.Equals(downloadingId, _downloadingQualityId, StringComparison.Ordinal))
        {
            _downloadingQualityId = downloadingId;
            _abr.SeedCurrent(downloadingIndex);
            if (string.Equals(_pendingRepresentationId, downloadingId, StringComparison.Ordinal)) _pendingRepresentationId = null;
            _gate.ProbeLanded(_abr.ThroughputSamples);
        }

        if (_pendingRepresentationId is not null && now - _pendingRepresentationTicks > RepresentationPendingTimeoutMs)
        {
            _player.LogDiagnostic($"representation '{_pendingRepresentationId}' never became downloading within " +
                                  $"{RepresentationPendingTimeoutMs}ms — releasing the ABR gate");
            _pendingRepresentationId = null;
        }
        // A probe whose switch is no longer pending and whose rung is not being downloaded never happened (rejected by
        // native, failed, timed out): it is neither a success nor a failure, and nothing may wait for its evidence.
        if (_gate.ProbeUnjudged && _pendingRepresentationId is null
            && !string.Equals(_downloadingQualityId, _probeRungId, StringComparison.Ordinal))
        {
            _gate.EndProbe();
            _abr.DeclineLastDecision(Math.Max(0, IndexOfVariant(_downloadingQualityId)));
        }

        if (!_qualitySelection.IsAuto || now - _lastAbrTicks < 1_000) return;
        _lastAbrTicks = now;
        // F139: no decision while a switch is still on its way. The controller writes its pick into its own state before
        // native has applied anything, so deciding again here compared a pick that did not exist yet and reverted a forced
        // probe one second after it was requested, before a single byte of its rung had been measured.
        if (_pendingRepresentationId is not null) return;
        long bufferedMs = Math.Max(0, _player.ForwardBufferedMs);
        if (_gate.HoldForProbeEvidence(_abr.ThroughputSamples, bufferedMs)) return;
        QualityVariant downloading = (downloadingIndex >= 0 ? _qualityVariants[downloadingIndex] : _activeQuality)
                                     ?? _qualityVariants[0];
        // Inside the minimum switch interval only a decision that could be an emergency, or a cap, is worth asking the
        // controller for: a refused pick would have to be reverted and would reset the votes it had just counted.
        if (!_gate.IntervalElapsed(now) && bufferedMs >= _gate.EmergencyBufferMs
            && downloading.Resolution.Height <= _abr.MaxHeight) return;
        int chosen = _abr.Choose(_qualityVariants, TimeSpan.FromMilliseconds(bufferedMs));
        QualityVariant pick = _qualityVariants[Math.Clamp(chosen, 0, _qualityVariants.Length - 1)];
        if (string.Equals(pick.Id, downloading.Id, StringComparison.Ordinal)) return;

        AbrDecisionReason reason = _abr.LastDecisionReason;
        bool isDecrease = pick.Bitrate < downloading.Bitrate;
        if (!_gate.Allows(now, isDecrease, bufferedMs, reason))
        {
            _abr.DeclineLastDecision(Math.Max(0, IndexOfVariant(downloading.Id)));
            return;
        }

        // A decrease or a cap change appends after everything buffered (nothing discarded). An upswitch right after a
        // viewport raise keeps only ViewportUpswitchRetainMs, so the sharper picture shows soon.
        int retainMs = IProtectedVideoPlayer.AppendAtBufferEnd;
        if (_viewportRaised)
        {
            if (!isDecrease && now - _viewportRaiseTicks <= ViewportRaiseRetainWindowMs) retainMs = ViewportUpswitchRetainMs;
            if (!isDecrease) _viewportRaised = false;
        }
        // Always-on, and only on an actual rung change: the sample that fed the estimate, both EWMAs, the buffer,
        // the cap and WHY — the one line that explains an "Auto · 240p" from a field log with no repro.
        _player.LogDiagnostic(
            $"abr sample={_lastSampleBytes}B/{_lastSampleMs}ms={_lastSampleKbps:F0}kbps " +
            $"fast={_abr.FastKbps:F0} slow={_abr.SlowKbps:F0} n={_abr.ThroughputSamples} " +
            $"buffer={bufferedMs}ms cap={(_abr.MaxHeight == int.MaxValue ? "none" : _abr.MaxHeight.ToString())} " +
            $"active={_activeQuality?.Id ?? "-"} downloading={downloading.Id} -> idx={chosen} id={pick.Id} " +
            $"{pick.Resolution.Width}x{pick.Resolution.Height}@{pick.Bitrate} why={reason} retain={retainMs}ms");
        if (reason == AbrDecisionReason.ForcedProbe)
        {
            _probeRungId = pick.Id;
            _gate.BeginProbe();
        }
        RequestRepresentation(pick.Id, retainMs, now);
    }

    /// <summary>How long a requested-but-unacknowledged representation blocks further ABR decisions. Acknowledged when
    /// native reports the representation as DOWNLOADING (spliced into the buffer), which is a segment fetch away, not
    /// when its picture reaches the screen.</summary>
    private const int RepresentationPendingTimeoutMs = 12_000;

    /// <summary>The forward buffer an upswitch keeps after a viewport raise, and how long after the raise it applies.</summary>
    private const int ViewportUpswitchRetainMs = 25_000;
    private const long ViewportRaiseRetainWindowMs = 15_000;

    /// <summary>A manual pin lands at the boundary right after the playhead: the user asked for that rung NOW.</summary>
    private const int PinRetainMs = 0;

    private int IndexOfVariant(string? id)
    {
        if (id is null) return -1;
        for (int i = 0; i < _qualityVariants.Length; i++)
            if (string.Equals(_qualityVariants[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    private void RequestRepresentation(string id, int retainMs, long now)
    {
        _pendingRepresentationId = id;
        _pendingRepresentationTicks = now;
        _gate.NoteSwitch(now);
        try
        {
            ValueTask pending = _player.SelectVideoRepresentationAsync(id, retainMs);
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
        _sizeRetry?.Dispose();
        PumpRequested = null;
        _player.PumpRequested -= OnPlayerPumpRequested;
        // Detach + destroy are posted native work items (non-blocking); the runtime and its engine stay warm.
        try { _player.Stop(); } catch { }
        try { _player.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
