using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The dead-simple facade (spec §4.1): a thin owner that holds the currently-routed backend session, forwards its state
/// signals through a shared <see cref="MediaPlayerCore"/>, and gives every open a FRESH backend session: the previous
/// session is disposed first, never reused by <see cref="MediaKind"/> (backends may share their expensive device/decoder
/// factories internally). Opens are last-wins and dispose-safe: a newer open, <see cref="Stop"/> or
/// <see cref="DisposeAsync"/> supersedes an open still in flight, and its late session is disposed instead of connected.
/// <c>Play(source)</c> is the whole 90% case.
/// <para>M0: the concrete video/audio backends land in M1/M2 — until one is registered on the <see cref="MediaRouter"/>,
/// <c>OpenAsync</c> surfaces an honest <see cref="MediaError.NoBackend"/> rather than pretending. The routing +
/// signal-forwarding + backend-swap wiring is fully real and exercised headlessly via a registered test backend. A host
/// that ships platform backends (the Windows host) installs them once through <see cref="MediaRouter.SetDefaultRegistrar"/>,
/// and every <see cref="Create"/>/<see cref="Build"/> player then resolves them with no registration of its own.</para>
/// <para><b>The control plane belongs to the player, not to an element (F132).</b> A session that has video raises
/// <see cref="IVideoPumpSource.PumpRequested"/> from its engine thread; the player turns each raise into ONE coalesced
/// UI-thread state pump of its own (state, position, duration, natural size, errors, seek landing, the DRM phase), through
/// the poster captured at construction, so the signals advance with no <c>MediaPlayerElement</c> mounted (a hand-off
/// between windows, a covered presenter, a closed pop-out). A mounted element only binds the surface and places it
/// (<see cref="PumpVideo"/>); it is told about the same raise right after the state pump has been queued.</para>
/// </summary>
public sealed class MediaPlayer : IMediaPlayer, IAsyncDisposable, IVideoPumpSource
{
    private readonly MediaPlayerCore _core = new();
    private readonly MediaSignalSink _sink;
    private readonly MediaRouter _router;
    private readonly NetworkOptions? _network;
    private readonly BufferPolicy? _buffering;
    private readonly IAbrPolicy? _abr;
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _licenseRelay;
    private readonly Dictionary<int, CueTrack> _captionTracks = new();
    private CueTrack? _activeCaptions;

    private IMediaSession? _session;
    private IVideoPumpSource? _videoPumpSource;
    private MediaKind _currentKind = MediaKind.Auto;
    private volatile bool _disposed;
    // The UI poster of the host that was live when this player was built, captured ONCE: the marshal target belongs to the
    // player, not to whichever host last wrote HostDispatch.Current (a pop-out child used to overwrite and then null it).
    private readonly Action<Action>? _uiPost = FluentGpu.Hooks.HostDispatch.Current;

    // The player's own state pump (F132). _statePumpQueued coalesces a burst of raises into one posted turn (cleared before
    // the turn runs, so a raise during it queues the next one); _statePumpRunning/_statePumpAgain only matter where there
    // is no poster (headless: the request runs inline, and a session that raises from inside its own pump folds the nested
    // request into one more turn instead of recursing). The age is read from any thread by diagnostics.
    private const int MaxStatePumpTurns = 4;
    private int _statePumpQueued;
    private int _statePumpRunning;
    private int _statePumpAgain;
    private int _statePumps;
    private long _lastStatePumpMs;   // Environment.TickCount64 of the last state pump, or of the session connect; 0 = no session
    private readonly Action _statePumpTurn;

    // Open ordering (last wins). _openGeneration names the one open that may still publish; a newer open, Stop and
    // DisposeAsync advance it and cancel _openCts, so a session that finishes opening late is disposed, never connected.
    // _openGate makes "is this open still current → start its session" atomic against that advance.
    private readonly object _openGate = new();
    private int _openGeneration;
    private CancellationTokenSource? _openCts;
    // The sidecar-subtitle fetch of the CURRENT open, once that open has returned (it runs detached from the open). Advanced
    // with the generation: a newer open, Stop and DisposeAsync cancel it so its requests stop with the source that asked.
    private CancellationTokenSource? _subtitleCts;
    // Sessions Stop() released that are still disposing in the background; DisposeAsync awaits it (the engine must be
    // returned before the next lease).
    private Task _stopDisposals = Task.CompletedTask;

    internal MediaPlayer(MediaRouter router, NetworkOptions? network, BufferPolicy? buffering,
                         IAbrPolicy? abr, Func<LicenseRequest, ValueTask<LicenseResponse>>? licenseRelay)
    {
        _router = router;
        _network = network;
        _buffering = buffering;
        _abr = abr;
        _licenseRelay = licenseRelay;
        _sink = new MediaSignalSink(_core);
        _statePumpTurn = StatePumpTurn;
    }

    /// <summary>Create a player with working defaults; the backend is resolved on the first <c>Play</c> from the host's
    /// default registrar (<see cref="MediaRouter.SetDefaultRegistrar"/>). With no host registrar installed (headless, tests)
    /// the router is empty and an open surfaces <see cref="MediaError.NoBackend"/>.</summary>
    public static MediaPlayer Create() => new(MediaRouter.CreateWithDefaults(), null, null, null, null);
    /// <summary>Start the Layer-2 power path (spec §4.1).</summary>
    public static MediaPlayerBuilder Build() => new();

    /// <summary>The underlying core (for the SMTC bridge and other consumers of the same headless signals).</summary>
    public MediaPlayerCore Core => _core;
    /// <summary>The current routed session (null before the first successful open).</summary>
    public IMediaSession? Session => _session;
    /// <inheritdoc/>
    public event Action? PumpRequested;

    /// <summary>How many state pumps this player has run for its video sessions (see the class remarks). A diagnostic: a
    /// playing video whose count stops moving has a control plane that is not being driven.</summary>
    public int StatePumpCount => Volatile.Read(ref _statePumps);

    /// <summary>Milliseconds since the connected video session was last state-pumped, or since it connected when it has not
    /// been yet; 0 while no video session is connected. Safe from any thread. A PLAYING session raises at least about once a
    /// second (the engines' position tick), so a large value here means state, position and errors have stopped publishing.</summary>
    public long StatePumpAgeMs
    {
        get
        {
            long last = Volatile.Read(ref _lastStatePumpMs);
            return last == 0 ? 0 : Math.Max(0, Environment.TickCount64 - last);
        }
    }

    // ── the one-call easy path (all funnel into Play(MediaSource)) ───────────────────────────────────────────────────

    /// <summary>Play a URI or local path (auto buffering / SMTC / default tracks).</summary>
    public ValueTask Play(string uriOrPath)
        => Play(LooksLikeUri(uriOrPath) ? MediaSource.FromUri(uriOrPath, _network) : MediaSource.FromFile(uriOrPath));
    /// <summary>Play a stream.</summary>
    public ValueTask Play(Stream stream) => Play(MediaSource.FromStream(stream));
    /// <summary>Play in-memory bytes.</summary>
    public ValueTask Play(ReadOnlyMemory<byte> bytes) => Play(MediaSource.FromBytes(bytes));
    /// <summary>The general form all overloads funnel into: open then play.</summary>
    public ValueTask Play(MediaSource source) => Play(source, CancellationToken.None);

    /// <summary>Open then play, abandoning the play (not the open's own last-wins bookkeeping) when
    /// <paramref name="ct"/> cancels: a caller that re-targets the player before this open lands (<c>UseVideo</c>) cancels
    /// the old call so its tail never starts playback for a source nobody wants any more.</summary>
    public async ValueTask Play(MediaSource source, CancellationToken ct)
    {
        await OpenAsync(source, ct).ConfigureAwait(false);
        // OpenAsync's final continuation lands on a pool thread; PlayAsync writes _core synchronously, so marshal it.
        await OnUiAsync(() => { if (!ct.IsCancellationRequested && _core.Error.Peek() is null) _ = PlayAsync(); }).ConfigureAwait(false);
    }

    private static bool LooksLikeUri(string s) => s.Contains("://", StringComparison.Ordinal);

    // A session may raise this from a native/media worker thread. The player queues its own state pump FIRST (the posts run
    // in order), then tells the mounted element, which marshals to the UI thread before requesting the registry pump: by
    // the time the element binds and places, the state this raise announced has been published. The facade does no scene
    // work here.
    private void OnSessionPumpRequested() => RequestVideoPump();
    private void RequestVideoPump()
    {
        if (_disposed) return;
        RequestStatePump();
        PumpRequested?.Invoke();
    }

    // Asks for ONE UI-thread state pump. Any thread. With a poster the requests coalesce into a single posted turn; with
    // none (headless/test: no cross-thread hop to make) the pump runs right here. It asks only when there is something to
    // pump (a video session, or captions following the position): an audio-only player, and the Stop / Close resets that
    // run with no session, cost no UI hop, so every transport verb stays free of one and an open still posts two hops.
    private void RequestStatePump()
    {
        if (_session is not IVideoSurfaceSession && _activeCaptions is null) return;
        var post = _uiPost ?? FluentGpu.Hooks.HostDispatch.Current;
        if (post is null) { RunStatePump(); return; }
        if (Interlocked.Exchange(ref _statePumpQueued, 1) != 0) return;
        post(_statePumpTurn);
    }

    private void StatePumpTurn()
    {
        Volatile.Write(ref _statePumpQueued, 0);
        RunStatePump();
    }

    private void RunStatePump()
    {
        if (Interlocked.Exchange(ref _statePumpRunning, 1) != 0) { Volatile.Write(ref _statePumpAgain, 1); return; }
        try
        {
            for (int turn = 0; turn < MaxStatePumpTurns; turn++)
            {
                Volatile.Write(ref _statePumpAgain, 0);
                PumpStateOnce();
                if (Volatile.Read(ref _statePumpAgain) == 0) break;
            }
        }
        finally { Volatile.Write(ref _statePumpRunning, 0); }
    }

    // One state pump: the session publishes its state through the sink (no surface, no element), then the active captions
    // follow the position it just published. UI thread (or inline where there is no poster).
    private void PumpStateOnce()
    {
        if (_disposed) return;
        IMediaSession? session = _session;
        if (session is IVideoSurfaceSession surface)
        {
            surface.PumpState();
            Volatile.Write(ref _lastStatePumpMs, Environment.TickCount64);
            Interlocked.Increment(ref _statePumps);
        }
        if (_activeCaptions is { } captions)
        {
            captions.Advance(_core.Position.Peek());
            _core.SetActiveCue(captions.ActiveCue.Peek());
        }
    }

    private void AttachVideoPumpSource(IMediaSession session)
    {
        DetachVideoPumpSource();
        if (session is not IVideoPumpSource source) return;
        _videoPumpSource = source;
        source.PumpRequested += OnSessionPumpRequested;
        Volatile.Write(ref _lastStatePumpMs, Environment.TickCount64);   // the age of a session that has not been pumped yet counts from its connect
    }

    private void DetachVideoPumpSource()
    {
        Volatile.Write(ref _lastStatePumpMs, 0);   // no session, no age
        if (_videoPumpSource is not { } source) return;
        source.PumpRequested -= OnSessionPumpRequested;
        _videoPumpSource = null;
    }

    // ── IMediaPlayer reactive surface (forwarded to the shared core) ─────────────────────────────────────────────────
    /// <inheritdoc/>
    public IReadSignal<PlaybackState> State => _core.State;
    /// <inheritdoc/>
    public IReadSignal<bool> IsPlayRequested => _core.IsPlayRequested;
    /// <inheritdoc/>
    public IReadSignal<SuppressionReason> Suppression => _core.Suppression;
    /// <inheritdoc/>
    public IReadSignal<bool> IsPlaying => _core.IsPlaying;
    /// <inheritdoc/>
    public IReadSignal<bool> IsBuffering => _core.IsBuffering;
    /// <inheritdoc/>
    public FloatSignal PositionSeconds => _core.PositionSeconds;
    /// <inheritdoc/>
    public IReadSignal<TimeSpan> Position => _core.Position;
    /// <inheritdoc/>
    public IReadSignal<TimeSpan> Duration => _core.Duration;
    /// <inheritdoc/>
    public IReadSignal<BufferHealth> Buffer => _core.Buffer;
    /// <inheritdoc/>
    public IReadSignal<BufferingInfo> Buffering => _core.Buffering;
    /// <inheritdoc/>
    public IReadSignal<TimelineInfo> Timeline => _core.Timeline;
    /// <inheritdoc/>
    public IReadSignal<SizeI> NaturalSize => _core.NaturalSize;
    /// <inheritdoc/>
    public IReadSignal<VideoGeometry> VideoGeometry => _core.VideoGeometry;
    /// <inheritdoc/>
    public IReadSignal<VideoColorInfo> VideoColor => _core.VideoColor;
    /// <inheritdoc/>
    public IReadSignal<VideoSurfaceGeometry> SurfaceGeometry => _core.SurfaceGeometry;
    /// <inheritdoc/>
    public IReadSignal<PlaybackStatistics> Statistics => _core.Statistics;
    /// <inheritdoc/>
    public IReadSignal<TimedCue?> ActiveCue => _core.ActiveCue;
    /// <inheritdoc/>
    public IReadSignal<MediaError?> Error => _core.Error;
    /// <inheritdoc/>
    public FloatSignal Volume => _core.Volume;
    /// <inheritdoc/>
    public IReadSignal<bool> Muted => _core.Muted;
    /// <inheritdoc/>
    public FloatSignal Rate => _core.Rate;
    /// <inheritdoc/>
    public TrackSet Tracks => _core.Tracks;
    /// <inheritdoc/>
    public PlayQueue Queue => _core.Queue;
    /// <inheritdoc/>
    public IAudioEffects Effects => _core.Effects;
    /// <inheritdoc/>
    public NowPlaying NowPlaying => _core.NowPlaying;
    /// <inheritdoc/>
    public MediaCommands Commands => _core.Commands;
    /// <inheritdoc/>
    public QualitySet Qualities => _core.Qualities;
    /// <inheritdoc/>
    public IReadSignal<VideoSurfaceId> VideoSurface => _core.VideoSurface;

    /// <summary>The element's turn: bind the surface handle, size the stream and place the child (the GEOMETRY half of the
    /// session's pump). It publishes no state, position or error: the player runs the state half itself, from the
    /// session's own pump requests (see the class remarks), so nothing is published twice and nothing waits for an element.</summary>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed) return;
        // The state half must have run before the geometry half acts on it, whichever host's queue drains first: a pop-out
        // element posts through its own host while the state pump posts to the one this player was built on, and nothing
        // orders those two queues. A FORMATCHANGE / RESOURCELOST epoch the geometry half has not seen adopted is skipped
        // (and nobody asks again), so a state turn that is still queued runs HERE, first. The queued turn then finds
        // nothing new: the pump is value-gated, and a nested or concurrent run only folds into _statePumpAgain.
        if (Volatile.Read(ref _statePumpQueued) != 0) RunStatePump();
        // Only a composited-video session (the MF backend) drives the surface handoff; everything else is a no-op.
        (_session as IVideoSurfaceSession)?.PumpGeometry(binding, videoRect, scale);

        // Preserve active-presentation diagnostics. This no longer wakes the host: native DirectComposition video
        // presents decoded frames independently, while the UI playhead owns its explicit FrameClock subscription.
        // "Presenting" means the user intends playback AND the session is either advancing (Playing) or ramping toward
        // it (Opening/Buffering — e.g. the DRM/CDM licensing handshake). Gated so paused/stopped/audio-only players
        // report false:
        //  • audio-only sessions don't implement IVideoSurfaceSession (video-capable check below);
        //  • a resolved audio-only MF source (video-capable but no natural size) only counts while still ramping.
        // Read back after the pump so it reflects the state the session just published.
        if (binding.IsValid)
        {
            bool videoCapable = _session is IVideoSurfaceSession;
            var st = _core.State.Peek();
            bool hasVideo = !_core.NaturalSize.Peek().IsEmpty;
            // Ramping = the DRM/CDM licensing handshake / initial buffer, where the natural size may not be known yet.
            // ActivePlayback = a resolved video the user intends to play:
            // Playing (advancing), Ready, OR Paused — Paused is CRITICAL: on resume, IsPlayRequested flips true while the
            // state is still Paused (the native engine has not acknowledged resume yet). Play-intent false (user paused)
            // and Idle/Ended/Failed report not-presenting.
            bool ramping = st is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled;
            bool activePlayback = hasVideo && st is PlaybackState.Ready or PlaybackState.Paused or PlaybackState.Playing;
            bool presenting = videoCapable && _core.IsPlayRequested.Peek() && (ramping || activePlayback);
            binding.SetPresenting(presenting);
        }
    }

    // ── transport (forward to the routed session; keep intent coherent on the core) ──────────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.SetPlayRequested(true);
        RequestVideoPump();
        return _session?.PlayAsync() ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.SetPlayRequested(false);
        RequestVideoPump();
        return _session?.PauseAsync() ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_disposed) return;
        // An open still in flight must not resurrect the stopped player: it is superseded, so its session is disposed.
        SupersedeOpen(null);
        DetachVideoPumpSource();
        IMediaSession? session = Interlocked.Exchange(ref _session, null);
        // The pause is issued on the calling thread like any transport verb; the dispose finishes in the background
        // (DisposeAsync awaits it). Without releasing the session its next pump would republish the real state over Idle.
        if (session is not null) TrackStopDisposal(PauseAndDisposeAsync(session));
        _activeCaptions = null;   // the released session's captions would re-publish a cue from the next PumpVideo
        _core.SetActiveCue(null);
        _core.SetVideoSurface(default);   // its surface is gone with it
        _core.SetPlayRequested(false);
        _core.SetState(PlaybackState.Idle);
        _core.SettleTransport();
        RequestVideoPump();
    }

    private void TrackStopDisposal(Task disposal)
    {
        lock (_openGate) _stopDisposals = _stopDisposals.IsCompleted ? disposal : Task.WhenAll(_stopDisposals, disposal);
    }

    private static async Task PauseAndDisposeAsync(IMediaSession session)
    {
        try { await session.PauseAsync().ConfigureAwait(false); }
        catch { /* best effort: the dispose below releases the session either way */ }
        await DisposeOffThreadAsync(session).ConfigureAwait(false);
    }

    // Disposes a session the player no longer owns (stale open, Stop) on a pool thread — never the UI thread — and never
    // throws: a failing dispose of a superseded session must not surface to the open or caller that superseded it.
    private static async Task DisposeOffThreadAsync(IMediaSession session)
    {
        try { await Task.Run(async () => await session.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false); }
        catch { /* teardown never throws */ }
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode = SeekMode.Accurate)
    {
        if (_disposed) return ValueTask.CompletedTask;
        RequestVideoPump();
        return _session?.SeekAsync(to, mode) ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask StepFrame(int delta)
    {
        if (_disposed) return ValueTask.CompletedTask;
        RequestVideoPump();
        return _session?.SeekAsync(_core.Position.Peek() + TimeSpan.FromMilliseconds(33.0 * delta), SeekMode.Accurate) ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void SetRate(double rate) { if (_disposed) return; rate = WsolaAudioSource.ClampRate(rate); _core.Rate.Value = (float)rate; _session?.SetRate(rate); RequestVideoPump(); }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (_disposed) return; _core.Volume.Value = (float)Math.Clamp(volume, 0, 1); _session?.SetVolume(volume); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (_disposed) return; _core.SetMuted(muted); _session?.SetMuted(muted); }

    /// <inheritdoc/>
    public async ValueTask SelectTrackAsync(MediaTrack? track)
    {
        if (_disposed || _session is null) return;
        await _session.SelectTrackAsync(track).ConfigureAwait(false);
        // Resumes on a pool thread — marshal the core writes to the sole writer.
        await OnUiAsync(() =>
        {
            if (track is null)
            {
                _core.Tracks.DisableText();
                _activeCaptions = null;
                _core.SetActiveCue(null);
            }
            else
            {
                _core.Tracks.Select(track);
                if (track.Kind == TrackKind.Text)
                {
                    _captionTracks.TryGetValue(track.Id, out _activeCaptions);
                    _core.SetActiveCue(null);
                }
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SelectQualityAsync(QualitySelection selection)
    {
        if (_disposed || _session is null) return;
        await _session.SelectQualityAsync(selection).ConfigureAwait(false);
        // Resumes on a pool thread — marshal the publish to the sole writer.
        await OnUiAsync(() => _core.Qualities.PublishSelection(selection)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void SetAdaptiveViewportHeight(int height) => _session?.SetAdaptiveViewportHeight(height);

    /// <inheritdoc/>
    public void SetAdaptiveMaxHeight(int height) => _session?.SetAdaptiveMaxHeight(height);

    /// <inheritdoc/>
    public ValueTask GoLiveAsync()
        => _disposed ? ValueTask.CompletedTask : (_session?.GoLiveAsync() ?? SeekAsync(_core.Timeline.Peek().LiveEdge));

    /// <inheritdoc/>
    public ValueTask PreviousChapterAsync() => SeekChapter(-1);

    /// <inheritdoc/>
    public ValueTask NextChapterAsync() => SeekChapter(+1);

    private ValueTask SeekChapter(int direction)
    {
        var chapters = _core.Timeline.Peek().Chapters;
        if (_disposed || chapters.Count == 0) return ValueTask.CompletedTask;
        TimeSpan pos = _core.Position.Peek();
        if (direction < 0)
        {
            for (int i = chapters.Count - 1; i >= 0; i--)
                if (chapters[i].Start < pos - TimeSpan.FromSeconds(2)) return SeekAsync(chapters[i].Start);
            return SeekAsync(chapters[0].Start);
        }
        for (int i = 0; i < chapters.Count; i++)
            if (chapters[i].Start > pos + TimeSpan.FromMilliseconds(250)) return SeekAsync(chapters[i].Start);
        return ValueTask.CompletedTask;
    }

    // ── source + queue + preroll ─────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask OpenAsync(MediaSource source, CancellationToken ct = default) => OpenCoreAsync(source, null, ct);

    /// <inheritdoc/>
    public ValueTask OpenAsync(MediaSource source, MediaOpenOptions options, CancellationToken ct = default)
        => OpenCoreAsync(source, options, ct);

    private async ValueTask OpenCoreAsync(MediaSource source, MediaOpenOptions? caller, CancellationToken ct)
    {
        if (_disposed) return;
        // Last open wins: this open supersedes (and cancels) any open still in flight, and Stop/DisposeAsync supersede it in
        // turn. The linked token is what the backend and the subtitle fetch observe; the generation gates every publish.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int generation = SupersedeOpen(cts);
        IMediaSession? unclaimed = null;   // the opened session, until the connect post below has taken ownership of it
        bool claimed = false;
        try
        {
            var kind = MediaKindSniffer.Sniff(source);
            var backend = _router.Resolve(kind);
            if (backend is null)
            {
                _core.SetError(MediaError.NoBackend(kind));
                _core.SetState(PlaybackState.Failed);
                return;
            }

            // A source open owns a fresh backend session. Backends may reuse their expensive device/decoder factories
            // internally, but retaining the previous live session here leaks clocks, network requests and native handles.
            if (Interlocked.Exchange(ref _session, null) is { } old)
            {
                DetachVideoPumpSource();
                await old.DisposeAsync().ConfigureAwait(false);
            }
            // A session Stop() released may still be disposing in the background: wait for it too, so its engine is back
            // before the backend leases one for this open (the backends' return-before-lease sequencing). Never faults.
            Task stopDisposals;
            lock (_openGate) stopDisposals = _stopDisposals;
            await stopDisposals.ConfigureAwait(false);
            if (!IsCurrentOpen(generation)) return;

            var opts = new MediaOpenOptions
            {
                // The caller decides WHERE and HOW the source opens (a song→video switch opens the video paused AT the song's
                // position, so the first frame is never 0:00); the facade's own policies fill everything the caller left unset.
                StartPaused = caller?.StartPaused ?? true,
                StartPosition = caller is { StartPosition: var start } && start > TimeSpan.Zero ? start : TimeSpan.Zero,
                Buffering = caller?.Buffering ?? _buffering,
                Network = caller?.Network ?? source.Network ?? _network,
                Abr = caller?.Abr ?? _abr,
                LiveLatency = source is AdaptiveSource adaptive ? adaptive.Options.LatencyMode : LiveLatencyMode.Standard,
                // The caller's live-ness declaration (MediaSource.WithLiveness) outranks any backend inference — see
                // SourceLiveness. Auto (the default) leaves every backend exactly as it was.
                Liveness = source.Liveness,
                LicenseRelay = caller?.LicenseRelay ?? _licenseRelay
            };

            // The Opening publish is posted WITHOUT waiting for the UI thread (backend.OpenAsync starts now, not a frame
            // later) and is generation-tagged, so a superseded open never publishes it. The posts run in order: this one,
            // then the single post below that connects the session.
            TimeSpan startAt = opts.StartPosition;
            _ = OnUiAsync(() =>
            {
                if (!IsCurrentOpen(generation)) return;
                _core.SetError(null);
                _core.SetState(PlaybackState.Opening);
                // A fresh open resets everything the PREVIOUS source left on the core (IMediaPlayer.VideoSurface's own contract:
                // "IsNone until the first video frame" — the headless player already does this). In-place switches keep this
                // MediaPlayer, so without it the old surface keeps the element's hole punched and its poster down over the
                // held outgoing frame, and the old natural size reads as "a frame was seen" to every first-frame heuristic.
                // The new session republishes each of these from its own metadata. Known consequence: MediaPlayerElement reads an
                // empty NaturalSize as audio-only (IsAudioOnly), so during the Opening leg of a Switch holeActive goes false
                // and PumpNow hides the DComp child: the outgoing frame is replaced by the stage colour at once while the poster
                // fades in, instead of crossfading over the held frame. A host with its own poster (Wavee) masks it.
                _core.SetVideoSurface(default);
                _core.SetNaturalSize(SizeI.Zero);
                _core.SetDuration(TimeSpan.Zero);
                _activeCaptions = null;   // the old source's captions would re-publish a cue from the next PumpVideo
                _core.SetActiveCue(null);
                _core.SetPosition(TimeSpan.Zero);
                if (startAt > TimeSpan.Zero) _core.SetPosition(startAt);   // the caller's position, AFTER the reset
            });
            try
            {
                var session = await backend.OpenAsync(source, opts, cts.Token).ConfigureAwait(false);
                unclaimed = session;
                // Start the session NOW, on this thread, before any UI hop: a session whose real work is native (the
                // protected attach: segment fetch, licence) no longer waits for the post that connects its signals. Only
                // while this open is still current, atomically with the generation check: a stale session must never start
                // (a protected attach detaches whichever session is live on the native engine).
                bool live;
                lock (_openGate)
                {
                    live = IsCurrentOpen(generation);
                    if (live) session.Start();
                }
                if (!live) return;   // superseded while opening: the finally below disposes the session, unconnected
                // backend.OpenAsync resumes on whatever thread completed the open — a pool/native worker thread, once
                // ConfigureAwait(false) has dropped the original sync context. Session assignment, pump-source wiring,
                // and ConnectSignals's handful of signal writes into MediaPlayerCore all mutate state whose sole writer
                // must be the UI thread (MediaPlayerCore's threading doc / spec §12), so the whole mutation is ONE post
                // (also carrying the external-subtitle reset) rather than applied here in place. It is awaited so that
                // OpenAsync completing still means "the session is connected": a caller's PlayAsync/SetVolume right after
                // it must reach the session. The generation is re-checked inside the post — the authoritative check.
                await OnUiAsync(() =>
                {
                    // Check and claim under the gate: a Stop/DisposeAsync from another thread either supersedes first (the
                    // session stays unclaimed) or runs after the claim and takes the session out of _session itself.
                    lock (_openGate)
                    {
                        if (!IsCurrentOpen(generation)) return;
                        _session = session;
                        claimed = true;
                    }
                    _currentKind = kind;
                    AttachVideoPumpSource(session);
                    session.ConnectSignals(_sink);
                    session.SetRate(_core.Rate.Peek());
                    _captionTracks.Clear();   // the previous source's sidecar tracks (the subtitle load re-adds this source's)
                    _activeCaptions = null;
                    _core.SetActiveCue(null);
                    RequestVideoPump();
                }).ConfigureAwait(false);
                if (!claimed) return;
                // Sidecar subtitles load AFTER the open has finished, detached: the session is already running, so a slow or
                // unreachable subtitle host must not hold OpenAsync (and the Play that follows it) open for its timeout.
                if (source.ExternalSubtitles.Count > 0) StartExternalSubtitles(source, opts.Network, generation, ct);
            }
            catch (OperationCanceledException)
            {
                // Open canceled (dispose mid-open, superseded by a newer open or Stop) — complete quietly, never crash (spec §12).
            }
            catch (Exception ex)
            {
                // A superseded open's failure is not the player's: only the current open publishes.
                if (!IsCurrentOpen(generation)) return;
                // Same off-UI-thread hazard as above: this catch arm can run on a pool thread (an exception from
                // backend.OpenAsync, or rethrown from the OnUiAsync-marshaled block above), so the error/state publish
                // is marshaled too rather than written from here directly.
                var error = new MediaError(MediaErrorCategory.Source, ex.Message, null, new MediaLocus(null, source, null, null, null), MediaRecovery.Retryable);
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOpen(generation)) return;
                    _core.SetError(error);
                    _core.SetState(PlaybackState.Failed);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            // A session opened for a superseded/disposed open (or whose connect post never took it) is released here.
            if (!claimed && unclaimed is not null) await DisposeOffThreadAsync(unclaimed).ConfigureAwait(false);
            lock (_openGate) { if (ReferenceEquals(_openCts, cts)) _openCts = null; }
            cts.Dispose();
        }
    }

    // Advances the open generation (so every older open becomes stale) and installs the next open's token source, cancelling
    // the previous one. A newer open passes its own source; Stop and DisposeAsync pass null. Returns the new generation.
    private int SupersedeOpen(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource? previousSubtitles;
        int generation;
        lock (_openGate)
        {
            generation = ++_openGeneration;
            previous = _openCts;
            _openCts = next;
            previousSubtitles = _subtitleCts;
            _subtitleCts = null;
        }
        // The owning open (and the owning subtitle load) disposes its source in its finally, so a cancel can race a dispose.
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        try { previousSubtitles?.Cancel(); } catch (ObjectDisposedException) { }
        return generation;
    }

    private bool IsCurrentOpen(int generation) => !_disposed && Volatile.Read(ref _openGeneration) == generation;

    /// <summary>Marshal a <see cref="MediaPlayerCore"/> mutation onto the UI thread — its sole writer (spec §12
    /// thread-ownership table; see <see cref="MediaPlayerCore"/>'s own threading doc). <c>OpenAsync</c>'s
    /// post-open continuation and <see cref="LoadExternalSubtitlesAsync"/>'s post-fetch track registration both
    /// resume off the UI thread once a backend/network await drops the calling sync context, so both route their
    /// core writes through here instead of writing in place. Posts through the poster captured at construction (falling
    /// back to the process-static <see cref="FluentGpu.Hooks.HostDispatch.Current"/> for a player built before any host)
    /// and awaits the posted action's completion (not just its enqueue), so callers observe the mutation as applied once
    /// this returns. No host poster registered (headless/test — no cross-thread hop to make) ⇒ <paramref name="action"/>
    /// runs inline on the calling thread.</summary>
    private ValueTask OnUiAsync(Action action)
    {
        var post = _uiPost ?? FluentGpu.Hooks.HostDispatch.Current;
        if (post is null) { action(); return ValueTask.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return new ValueTask(tcs.Task);
    }

    // Starts the detached sidecar-subtitle load of an open that has connected its session. Owns a token source of its own
    // (linked to the open's caller token; the open's own source is disposed the moment OpenAsync returns): SupersedeOpen
    // cancels it, so a newer open, Stop and DisposeAsync stop the requests of the source that asked. Check-and-install
    // under the gate, atomically with the generation advance, so a supersede either precedes (nothing starts) or finds it.
    private void StartExternalSubtitles(MediaSource source, NetworkOptions? network, int generation, CancellationToken ct)
    {
        CancellationTokenSource cts;
        lock (_openGate)
        {
            if (!IsCurrentOpen(generation)) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _subtitleCts = cts;
        }
        _ = LoadExternalSubtitlesAsync(source, network, generation, cts);
    }

    // Runs detached from the open (StartExternalSubtitles), i.e. off the UI thread (see the OnUiAsync note above) — every
    // write here (the _captionTracks/_activeCaptions bookkeeping and the _core.Tracks publishes of the per-subtitle
    // post-fetch registration) is marshaled through OnUiAsync. Only the network fetches (SubtitleLoader.LoadAsync) run
    // off-thread, as intended: ALL of them start at once, and the tracks register in source order as each is ready (a
    // later subtitle that is already fetched registers without waiting). The previous source's tracks were already
    // cleared by the connect post, so a source with no sidecar subtitles never gets here. The registration is
    // generation-tagged: a superseded open never adds tracks to the newer source. Never faults: nobody observes this task.
    private async Task LoadExternalSubtitlesAsync(MediaSource source, NetworkOptions? network, int generation, CancellationTokenSource cts)
    {
        try
        {
            CancellationToken ct = cts.Token;
            IReadOnlyList<SubtitleSource> subtitles = source.ExternalSubtitles;
            var loads = new Task<CueTrack?>[subtitles.Count];
            for (int i = 0; i < loads.Length; i++) loads[i] = TryLoadSubtitleAsync(subtitles[i], network, ct);
            for (int i = 0; i < loads.Length; i++)
            {
                CueTrack? cues = await loads[i].ConfigureAwait(false);
                if (ct.IsCancellationRequested || !IsCurrentOpen(generation)) return;
                if (cues is not { } track) continue;
                SubtitleSource subtitle = subtitles[i];
                string label = Path.GetFileNameWithoutExtension(subtitle.Uri);
                if (string.IsNullOrWhiteSpace(label)) label = $"Subtitle {i + 1}";
                await OnUiAsync(() =>
                {
                    if (!IsCurrentOpen(generation)) return;
                    MediaTrack added = _core.Tracks.AddExternalSubtitle(subtitle, "und", label);
                    _captionTracks[added.Id] = track;
                    if (_activeCaptions is null)
                    {
                        _core.Tracks.Select(added);
                        _activeCaptions = track;
                    }
                }).ConfigureAwait(false);
            }
        }
        catch { /* A sidecar subtitle failure never takes down the primary audio/video session. */ }
        finally
        {
            lock (_openGate) { if (ReferenceEquals(_subtitleCts, cts)) _subtitleCts = null; }
            cts.Dispose();
        }
    }

    // One sidecar fetch: null for a failure, a timeout, or the open being superseded; the caller skips it and its siblings
    // keep loading.
    private static async Task<CueTrack?> TryLoadSubtitleAsync(SubtitleSource subtitle, NetworkOptions? network, CancellationToken ct)
    {
        try { return await SubtitleLoader.LoadAsync(MediaHttp.Shared, subtitle, network, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    /// <inheritdoc/>
    public void Enqueue(MediaSource next) { if (!_disposed) _core.Queue.Add(next); }

    /// <inheritdoc/>
    public PrepareToken PrepareNext(MediaSource next)
    {
        if (_disposed) return PrepareToken.None;
        var item = _core.Queue.Add(next);
        return new PrepareToken(item.Id, 0);
    }

    /// <summary>Detach: dispose the current session (its surface binding, clock, network requests and, for a protected
    /// source, the secure decoder are released) and return the facade to its just-built state, so the NEXT
    /// <see cref="OpenAsync(MediaSource, MediaOpenOptions, CancellationToken)"/> is a plain first open. Unlike
    /// <see cref="DisposeAsync"/> the facade stays usable and <see cref="PumpRequested"/> stays wired, so a host that
    /// parks between videos keeps ONE player (and one mounted media element) for the whole process instead of
    /// rebuilding both for every video. The rate, volume and mute the caller set are kept. Idempotent; a no-op once
    /// disposed. Call it from one thread at a time with <see cref="OpenAsync(MediaSource, CancellationToken)"/>.</summary>
    public async ValueTask CloseAsync()
    {
        if (_disposed) return;
        // An open still in flight must not reconnect a session after the close: it is superseded, so its session is disposed.
        SupersedeOpen(null);
        IMediaSession? old = null;
        // The session swap is the UI thread's (PumpVideo reads _session there), exactly like OpenCoreAsync's assignment.
        await OnUiAsync(() =>
        {
            DetachVideoPumpSource();
            old = _session;
            _session = null;
        }).ConfigureAwait(false);
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        // A session Stop() released may still be disposing: its engine must be back before the next lease.
        Task stopDisposals;
        lock (_openGate) stopDisposals = _stopDisposals;
        await stopDisposals.ConfigureAwait(false);
        // The session is gone: nothing the core still shows (position, size, tracks, an error) belongs to anything.
        await OnUiAsync(() =>
        {
            _core.SetPlayRequested(false);
            _core.SetError(null);
            _core.SetState(PlaybackState.Idle);
            _core.SetPosition(TimeSpan.Zero);
            _core.SetDuration(TimeSpan.Zero);
            _core.SetBuffer(BufferHealth.Empty);
            _core.SetBuffering(BufferingInfo.None);
            _core.SetTimeline(TimelineInfo.Empty);
            _core.SetNaturalSize(SizeI.Zero);
            _core.SetVideoGeometry(global::FluentGpu.Media.VideoGeometry.Empty);
            _core.SetVideoColor(VideoColorInfo.Sdr);
            _core.SetSurfaceGeometry(VideoSurfaceGeometry.Empty);
            _core.SetStatistics(PlaybackStatistics.Empty);
            _core.SetVideoSurface(default);
            _captionTracks.Clear();
            _activeCaptions = null;
            _core.SetActiveCue(null);
            _core.Tracks.Reset();
            _core.Qualities.Variants.Reset(Array.Empty<QualityVariant>());
            _core.Qualities.PublishSelection(QualitySelection.Auto);
            _core.Qualities.PublishActive(null);
            _core.SettleTransport();
            RequestVideoPump();
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancels an open still in flight; its late session is disposed by that open and never connected or started.
        SupersedeOpen(null);
        _core.SettleTransport();
        DetachVideoPumpSource();
        IMediaSession? session = Interlocked.Exchange(ref _session, null);
        Task stopDisposals;
        lock (_openGate) stopDisposals = _stopDisposals;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        // A session Stop() released may still be disposing: its engine must be back before the next lease.
        await stopDisposals.ConfigureAwait(false);
        PumpRequested = null;
    }
}

/// <summary>
/// The router (spec §4.1/§4.4): resolves a <see cref="MediaKind"/> to a registered <see cref="IMediaBackend"/>, with
/// <see cref="MediaKind.Auto"/> sniffing handled by <see cref="MediaKindSniffer"/> before resolution. Empty by default —
/// backends (MF video M1, PCM audio M2) register themselves; the facade surfaces a typed error for an unresolved kind.
/// A host that ships platform backends installs them as DEFAULTS (<see cref="SetDefaultRegistrar"/>): every router a
/// <see cref="MediaPlayer.Create"/>/<see cref="MediaPlayer.Build"/> makes is seeded with them, an explicit
/// <see cref="Register"/> on a kind always wins, and a default backend is only constructed when its kind is first resolved.
/// </summary>
public sealed class MediaRouter
{
    private readonly Dictionary<MediaKind, IMediaBackend> _backends = new();
    // Default backends: a factory per kind, built at the first Resolve of that kind and then reused. Guarded by its own
    // lock (Resolve runs on whichever thread opens), unlike the explicit table, which is only written at setup.
    private readonly Dictionary<MediaKind, DefaultBackend> _defaults = new();
    private static Action<MediaRouter>? s_defaultRegistrar;

    private sealed class DefaultBackend(Func<IMediaBackend> factory)
    {
        public readonly Func<IMediaBackend> Factory = factory;
        public IMediaBackend? Instance;
    }

    /// <summary>Install (or, with null, clear) the host's default-backend registrar: a callback that calls
    /// <see cref="RegisterDefault"/> for each platform backend it ships. The engine core cannot reference a platform
    /// project, so the host (the Windows <c>FluentApp</c>) hands it over once at startup. Applies to routers created after
    /// the call. Process-static: headless tests that install one reset it with <c>SetDefaultRegistrar(null)</c>.</summary>
    public static void SetDefaultRegistrar(Action<MediaRouter>? registrar) => Volatile.Write(ref s_defaultRegistrar, registrar);

    /// <summary>A router seeded by the host's default registrar (empty when none is installed).</summary>
    internal static MediaRouter CreateWithDefaults()
    {
        var router = new MediaRouter();
        Volatile.Read(ref s_defaultRegistrar)?.Invoke(router);
        return router;
    }

    /// <summary>Register a DEFAULT backend for a concrete kind: <paramref name="factory"/> runs at most once, when the kind is
    /// first resolved and no explicit <see cref="Register"/> covers it.</summary>
    public void RegisterDefault(MediaKind kind, Func<IMediaBackend> factory)
    {
        if (kind == MediaKind.Auto) throw new ArgumentException("Register a concrete kind, not Auto.", nameof(kind));
        ArgumentNullException.ThrowIfNull(factory);
        lock (_defaults) _defaults[kind] = new DefaultBackend(factory);
    }

    /// <summary>Register a backend for a concrete kind (PcmAudio / MfVideoOrFile).</summary>
    public void Register(MediaKind kind, IMediaBackend backend)
    {
        if (kind == MediaKind.Auto) throw new ArgumentException("Register a concrete kind, not Auto.", nameof(kind));
        _backends[kind] = backend;
    }

    /// <summary>Resolve a concrete kind to a backend, or null when none is registered.</summary>
    public IMediaBackend? Resolve(MediaKind kind)
    {
        if (_backends.TryGetValue(kind, out var b)) return b;
        lock (_defaults)
        {
            if (!_defaults.TryGetValue(kind, out var entry)) return null;
            return entry.Instance ??= entry.Factory();
        }
    }

    /// <summary>True when a backend (explicit or default) is registered for the kind.</summary>
    public bool Has(MediaKind kind)
    {
        if (_backends.ContainsKey(kind)) return true;
        lock (_defaults) return _defaults.ContainsKey(kind);
    }
}

/// <summary>Sniffs a source's routing kind (spec §5 <see cref="MediaKind.Auto"/>). An explicit <see cref="MediaSource.Kind"/>
/// always wins; otherwise the source shape/extension decides between the PCM audio graph and the MF video/file backend.</summary>
public static class MediaKindSniffer
{
    private static readonly string[] s_videoExt = { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".ts", ".m3u8", ".mpd", ".avi" };
    private static readonly string[] s_audioExt = { ".mp3", ".flac", ".ogg", ".oga", ".opus", ".wav", ".aac", ".m4a", ".alac" };

    /// <summary>Resolve <paramref name="source"/> to a concrete kind (never <see cref="MediaKind.Auto"/>).</summary>
    public static MediaKind Sniff(MediaSource source)
    {
        if (source.Kind != MediaKind.Auto) return source.Kind;
        return source switch
        {
            // Already-demuxed samples with a video stream ⇒ MF video/file; audio-only ⇒ the PCM graph.
            SampleSource ss => HasVideo(ss.Source) ? MediaKind.MfVideoOrFile : MediaKind.PcmAudio,
            // Raw callback bytes ⇒ the PCM audio graph (PlayPlay's shape).
            PullSource or FeedSource => MediaKind.PcmAudio,
            FileSource f => ByExtension(f.Path),
            UriSource u => ByExtension(u.Url),
            ClipSource c => Sniff(c.Inner),
            LoopSource l => Sniff(l.Inner),
            SilenceSource si => Sniff(si.Inner),
            ConcatSource cc when cc.Parts.Count > 0 => Sniff(cc.Parts[0]),
            MergeSource m when m.Tracks.Count > 0 => MediaKind.MfVideoOrFile,   // merged tracks ⇒ MF timeline
            // Self-contained files/streams default to the MF backend.
            _ => MediaKind.MfVideoOrFile
        };
    }

    private static bool HasVideo(IMediaSampleSource src)
    {
        var streams = src.Streams;
        for (int i = 0; i < streams.Count; i++) if (streams[i].Kind == StreamKind.Video) return true;
        return false;
    }

    private static MediaKind ByExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int q = path.IndexOf('?', StringComparison.Ordinal);
        string ext = dot >= 0 ? (q > dot ? path[dot..q] : path[dot..]).ToLowerInvariant() : "";
        foreach (var e in s_videoExt) if (ext == e) return MediaKind.MfVideoOrFile;
        foreach (var e in s_audioExt) if (ext == e) return MediaKind.PcmAudio;
        return MediaKind.MfVideoOrFile;
    }
}

/// <summary>The Layer-2 power-path builder (spec §4.1). Configures network/buffering/ABR/DRM defaults and backend
/// registrations, then <see cref="Build"/>s a <see cref="MediaPlayer"/>.</summary>
public sealed class MediaPlayerBuilder
{
    private readonly MediaRouter _router = MediaRouter.CreateWithDefaults();
    private NetworkOptions? _network;
    private BufferPolicy? _buffering;
    private IAbrPolicy? _abr;
    private Func<LicenseRequest, ValueTask<LicenseResponse>>? _licenseRelay;

    /// <summary>Set the default network options (auth/timeout).</summary>
    public MediaPlayerBuilder WithNetwork(NetworkOptions net) { _network = net; return this; }
    /// <summary>Set the buffering policy.</summary>
    public MediaPlayerBuilder WithBuffering(BufferPolicy policy) { _buffering = policy; return this; }
    /// <summary>Set the ABR policy.</summary>
    public MediaPlayerBuilder WithAbr(IAbrPolicy abr) { _abr = abr; return this; }
    /// <summary>Wire the DRM license relay (spec §9.2) — one async message→update, headlessly testable.</summary>
    public MediaPlayerBuilder WithDrm(Func<LicenseRequest, ValueTask<LicenseResponse>> licenseRelay) { _licenseRelay = licenseRelay; return this; }
    /// <summary>Register a backend for a concrete kind (M1 MF video, M2 PCM audio; or a test backend headlessly).</summary>
    public MediaPlayerBuilder WithBackend(MediaKind kind, IMediaBackend backend) { _router.Register(kind, backend); return this; }

    /// <summary>Build the configured player.</summary>
    public MediaPlayer Build() => new(_router, _network, _buffering, _abr, _licenseRelay);
}
