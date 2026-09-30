using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Pal;

namespace FluentGpu.Media.Windows;

/// <summary>
/// A live Media-Foundation video session (spec §9.1): drives an <see cref="IVideoEngine"/> (the PROVEN
/// <see cref="VideoMediaEngine"/> in production) and maps its POD <see cref="VideoEngineSnapshot"/> onto the player's
/// <see cref="MediaSignalSink"/>. State translation, transport, position projection and the composited-surface handoff all
/// run on the UI/pump thread (<see cref="PumpVideo"/>) so the sink's sole-writer contract holds.
///
/// <para><b>Snapshot out, commands in</b> (<c>docs/plans/video-smooth-switching-implementation.md</c> §1.4). This session
/// never blocks and never touches a ComPtr: <see cref="PumpVideo"/> reads exactly ONE <see cref="IVideoEngine.Snapshot"/>
/// per turn (a wait-free seqlock read) and every transport verb (<see cref="PlayAsync"/>/<see cref="PauseAsync"/>/
/// <see cref="SeekAsync"/>/rate/volume/mute/stream-rect/repaint) is a fire-and-forget POST through
/// <see cref="IVideoEngine.Commands"/> — accepted synchronously, realized by the engine thread on its own schedule, and
/// observed by the NEXT pump. There is no 10 Hz poll timer any more: the engine is the sole source of the position clock
/// (<see cref="VideoEngineSnapshot.PositionSeconds"/> sampled at <see cref="VideoEngineSnapshot.PositionTimestamp"/>,
/// extrapolated forward here by elapsed·rate while playing) and of the live DVR window (latched
/// <see cref="VideoEngineFlags.LiveSource"/>, re-published by the engine's own self-refresh) — nothing needs a
/// background nudge to stay fresh.</para>
/// <list type="bullet">
/// <item>Transport is idempotent and accepted SYNCHRONOUSLY — it records intent + posts to the engine and returns a
///   completed task; it never throws or blocks. The next <see cref="PumpVideo"/> realizes the resulting state
///   transition.</item>
/// <item>Position is projected from the engine's presentation clock (authoritative), pushed each pump.</item>
/// <item><see cref="Video"/> is <see cref="VideoDelivery.None"/> until the swap-chain handle + natural size exist, then a
///   <see cref="VideoDelivery.CompositedSurface"/> — the shipping Path A (spec §9.1).</item>
/// </list>
/// </summary>
public sealed class MfMediaSession : IMediaSession, IVideoSurfaceSession, IVideoPumpSource
{
    private readonly IVideoEngine _engine;
    // The source-switch generation THIS session owns (IVideoEngine.PostSetSource's return value). A snapshot whose
    // SourceEpoch does not match is either not-yet-caught-up (the engine hasn't drained the SetSource command yet) or
    // belongs to a LATER session sharing a warm-reused engine — either way, nothing here is safe to act on.
    private readonly int _sourceEpoch;
    private readonly MediaOpenOptions _opts;
    private readonly AdaptiveManifest? _manifest;
    // Returns the engine warm to MfMediaPlayer's pool (pause + detach — no teardown) instead of disposing it. Null only
    // in tests that construct a session directly; every production session (MfMediaPlayer.OpenAsync) supplies one.
    private readonly Action<IVideoEngine>? _release;

    private MediaSignalSink? _sink;
    private bool _disposed;

    // Intent (UI thread).
    private bool _playRequested;
    private bool _everPlayed;
    private double _rate = 1.0;
    private double _volume = 1.0;
    private bool _muted;

    // Realized/published state (UI thread, via the pump).
    private bool _metaReady;
    private SizeI _naturalSize = SizeI.Zero;
    // The last IVideoEngine.Snapshot.PresentationEpoch this session acted on. A mismatch means MF switched renditions
    // (FORMATCHANGE — a NEW decoded frame size), rebuilt its swap chain (RESOURCELOST), or the engine just accepted a
    // fresh SetSource (which bumps the same epoch) — re-ask for the natural size and re-assert the stream rect, or the
    // surface composites forever at the previous generation's geometry.
    private int _presentationEpoch;
    private TimeSpan _duration = TimeSpan.Zero;
    private nuint _handle;
    private VideoSurfaceId _publishedSurface;   // last value handed to sink.VideoSurface (PublishSurface)

    /// <summary>The player's VideoSurface signal — what MediaPlayerElement's poster/hole gate reads: non-None while
    /// a swap chain is bound for the live presentation, None otherwise. No real backend wrote this signal until
    /// 2026-09-22 (only the headless scripted player did), so the poster never dropped and the hole was never punched.
    /// Value-gated: a steady pump publishes nothing.</summary>
    private void PublishSurface(MediaSignalSink sink, VideoSurfaceId id)
    {
        if (id == _publishedSurface) return;
        _publishedSurface = id;
        sink.VideoSurface(id);
    }
    // The size (px) the video stream was last sized to inside MF's own swap chain — the (capped) NATURAL frame size,
    // NOT the destination rect: MF renders the full frame 1:1 into its swap chain and DirectComposition performs the
    // fit — see the §3 comment in PumpVideo.
    private int _streamW, _streamH;
    private PlaybackState _publishedState = PlaybackState.Opening;
    // The last command bitset actually pushed to the sink. Value-gated here so re-computing every pump costs a
    // comparison and publishes nothing when nothing changed.
    private MediaCommandFlags _publishedCommands;
    private bool _errorPublished;
    private bool _seeking;
    // Native DComp auto-presents decoded frames. This flag asks for a Repaint command only for an initial/reconfigured
    // hand-off or a real media-engine event, never once per host frame.
    private int _repaintPending = 1;

    /// <inheritdoc/>
    public event Action? PumpRequested;

    // In-band (manifest-declared) subtitle rendering: track-id → its adaptation, the loaded cue timeline, and the
    // last published cue. The cue timeline is built OFF-thread and swapped in as a whole (reference write); the pump
    // (UI thread) only ever reads a fully-built CueTrack. Unrelated to the engine seam — unchanged.
    private static readonly HttpClient s_subtitleHttp = new();
    private readonly Dictionary<int, AdaptiveTrackGroup> _textGroups = new();
    private volatile CueTrack? _inbandCues;
    private int _cueEpoch;
    private TimedCue? _publishedCue;
    // The pump publishes the authoritative playhead here (double is atomic on 64-bit; a torn read only mis-windows a
    // best-effort fetch by a segment). The off-thread windowed loader reads it to fetch cues AROUND the playhead.
    private volatile int _cuePlayheadMs;
    private const int CueSegmentsBehind = 1;   // scrub-back headroom
    private const int CueSegmentsAhead = 3;    // prefetch a few segments ahead of the playhead
    private const int CueWindowPollMs = 500;   // re-evaluate the window this often as playback advances

    internal MfMediaSession(IVideoEngine engine, int sourceEpoch, MediaOpenOptions opts,
        AdaptiveManifest? manifest = null, Action<IVideoEngine>? release = null)
    {
        _engine = engine;
        _sourceEpoch = sourceEpoch;
        _opts = opts;
        _manifest = manifest;
        _release = release;
        _playRequested = !opts.StartPaused;
        if (_playRequested) _everPlayed = true;
        _engine.StateChanged += OnEngineStateChanged;
    }

    // The engine's own self-refresh keeps the snapshot fresh (no poll timer here any more): a discrete transition
    // wakes this via StateChanged, and a live/resolving/playing source keeps refreshing itself on the engine thread's
    // own cadence — both paths converge on requesting one settled UI-thread pump.
    private void OnEngineStateChanged() => RequestPump();

    /// <summary>The caller DECLARED this source live (<see cref="SourceLiveness.Live"/>). Distinct from the engine's own
    /// latched <see cref="VideoEngineFlags.LiveSource"/> bit: only a DECLARED live source suppresses the duration
    /// outright, because on the Auto path the finite duration MF reports is all we have until the engine's own probe
    /// answers, and dropping it would regress every VOD URL.</summary>
    private bool ForcedLive => _opts.Liveness == SourceLiveness.Live;

    /// <summary>Whether THIS pump should treat the source as live via the no-manifest (engine-inferred) path — see the
    /// LIVE WITHOUT A MANIFEST note on <see cref="PublishEngineLiveTimeline"/>. A manifest-driven source computes its
    /// own live window and never consults this.</summary>
    private bool EngineLiveFor(in VideoEngineSnapshot snap)
    {
        if (_manifest is not null) return false;
        return _opts.Liveness switch
        {
            SourceLiveness.Vod => false,
            SourceLiveness.Live => true,
            _ => (snap.Flags & VideoEngineFlags.LiveSource) != 0,
        };
    }

    private void RequestPump(bool repaint = true)
    {
        if (_disposed) return;
        if (repaint) Volatile.Write(ref _repaintPending, 1);
        try { PumpRequested?.Invoke(); } catch { }
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        // Re-apply cold state the engine will accept once it catches up to THIS source, then announce we are opening
        // (metadata pending). Posts, not calls — accepted synchronously, realized on the engine's own schedule.
        _engine.Commands.Post(VideoCommandKind.Rate, a: _rate);
        _engine.Commands.Post(VideoCommandKind.Volume, a: _volume);
        _engine.Commands.Post(VideoCommandKind.Muted, i: _muted ? 1 : 0);
        sink.PlayRequested(_playRequested);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        PublishManifestCatalog(sink);
        // If StartPosition was requested, seek before the first frame (applied once the source resolves too).
        if (_opts.StartPosition > TimeSpan.Zero) _engine.Commands.Post(VideoCommandKind.Seek, a: _opts.StartPosition.TotalSeconds);
        RequestPump();
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _handle != 0 && !_naturalSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _naturalSize, IsHdr: false)
            : VideoDelivery.None;

    // ── transport (idempotent; accepted synchronously; posted; the pump realizes state) ────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        // RESUMING a live stream: jump to the live edge FIRST. A paused live source keeps its playhead where it was
        // while the DVR window slides on without it, so a plain Play() resumes minutes behind — and on a source that
        // does not retain that far back (Twitch) it resumes on bytes the server has already dropped and simply stalls.
        // Only on a genuine resume (play after a pause we already served), never on the initial play, which must honor
        // MediaOpenOptions.StartPosition.
        bool resuming = !_playRequested && _everPlayed;
        _playRequested = true;
        _everPlayed = true;
        if (resuming) SeekToLiveEdge();
        _engine.Commands.Post(VideoCommandKind.Transport, a: 1);
        _sink?.PlayRequested(true);
        RequestPump();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _engine.Commands.Post(VideoCommandKind.Transport, a: 0);
        _sink?.PlayRequested(false);
        RequestPump();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return ValueTask.CompletedTask;
        double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
        double t = Math.Clamp(to.TotalSeconds, 0.0, hi);
        _seeking = true;
        _sink?.Buffering(new BufferingInfo(BufferingReason.Seeking, -1, TimeSpan.Zero,
            (_opts.Buffering ?? BufferPolicy.Vod).ResumePlayback, false));
        // Keyframe = fast scrub-preview (approximate MF seek: snaps to the nearest keyframe, no exact-PTS decode);
        // Accurate = the final commit (normal/exact MF seek). SeekMode maps onto the Seek command's approximate flag.
        bool approximate = mode == SeekMode.Keyframe;
        _engine.Commands.Post(VideoCommandKind.Seek, a: t, i: approximate ? 1 : 0);
        if (Diag.Enabled)
            Diag.Event("media.seek", $"requested={to.TotalMilliseconds:0}ms clamped={t:0.000}s mode={mode} approx={approximate}");
        // Reflect the seek target immediately so a bound seekbar doesn't snap back for a frame.
        _sink?.Position(TimeSpan.FromSeconds(t));
        _sink?.SettleTransport();
        RequestPump();
        return ValueTask.CompletedTask;
    }

    /// <summary>Jump to the live edge of an engine-reported live source (the DVR window's end, minus a small backoff so
    /// the playhead lands on a segment MF actually holds rather than on the not-yet-published boundary). A no-op for a
    /// VOD source and for the manifest-driven live path, which computes its edge from the manifest and is untouched
    /// here.</summary>
    public ValueTask GoLiveAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        SeekToLiveEdge();
        return ValueTask.CompletedTask;
    }

    private void SeekToLiveEdge()
    {
        VideoEngineSnapshot snap = _engine.Snapshot;
        if (snap.SourceEpoch != _sourceEpoch || !EngineLiveFor(snap)) return;
        double target = LiveSessionRules.GoLiveTarget(snap.SeekableStart, snap.SeekableEnd);
        if (target <= 0) return;
        _engine.Commands.Post(VideoCommandKind.Seek, a: target, i: 0);   // exact — landing behind the edge is the fix
        if (Diag.Enabled)
            Diag.Event("media.golive", $"target={target:0.000}s window=[{snap.SeekableStart:0.000},{snap.SeekableEnd:0.000}]");
    }

    /// <inheritdoc/>
    public void SetRate(double rate)
    {
        if (_disposed) return;
        _rate = rate;
        _engine.Commands.Post(VideoCommandKind.Rate, a: rate);
        RequestPump();
    }
    /// <inheritdoc/>
    public void SetVolume(double volume)
    {
        if (_disposed) return;
        _volume = Math.Clamp(volume, 0, 1);
        _engine.Commands.Post(VideoCommandKind.Volume, a: _volume);
    }
    /// <inheritdoc/>
    public void SetMuted(bool muted)
    {
        if (_disposed) return;
        _muted = muted;
        _engine.Commands.Post(VideoCommandKind.Muted, i: muted ? 1 : 0);
        _sink?.Muted(muted);
    }

    /// <summary>Realize a TEXT selection by loading the manifest-declared rendition's WebVTT into a cue timeline the
    /// pump renders (in-band captions). Returns immediately; the fetch runs in the background and is superseded by the
    /// next selection (epoch). Audio/video selection is MF-engine-internal for natively-played adaptive URLs — a no-op
    /// here so the catalog selection still updates. Unrelated to the engine seam — unchanged.</summary>
    public ValueTask SelectTrackAsync(MediaTrack? track)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (track is null || track.Kind == TrackKind.Text)
        {
            int epoch = ++_cueEpoch;
            _inbandCues = null;
            if (track is not null && _textGroups.TryGetValue(track.Id, out AdaptiveTrackGroup? group))
                _ = LoadInbandCuesAsync(group, epoch);
        }
        return ValueTask.CompletedTask;
    }

    private async Task LoadInbandCuesAsync(AdaptiveTrackGroup group, int epoch)
    {
        try
        {
            // A live rendition is an ever-sliding window — whole-track prefetch does not apply (deferred).
            if (_manifest is not { } manifest || manifest.IsLive || group.Representations.Count == 0) return;
            AdaptiveRepresentation rep = group.Representations[0];
            IReadOnlyList<AdaptiveSegment> segments = rep.Segments;
            if (segments.Count == 0 && rep.PlaylistUri is { } playlist)
            {
                // HLS: the master only names the rendition; its media playlist carries the segment URIs.
                var mediaUri = new Uri(playlist);
                string text = await s_subtitleHttp.GetStringAsync(mediaUri).ConfigureAwait(false);
                AdaptiveManifest media = HlsManifestParser.ParseMedia(text, mediaUri, rep.Quality);
                if (media.TrackGroups.Count > 0 && media.TrackGroups[0].Representations.Count > 0)
                    segments = media.TrackGroups[0].Representations[0].Segments;
            }
            if (segments.Count == 0) return;

            // Playhead-windowed fetch (replaces the fetch-up-to-512-upfront storm): fetch only the segments AROUND the
            // current playhead — a few behind (scrub-back headroom) + a few ahead (prefetch) — advancing as playback
            // moves. Fetched cues are RETAINED (played-through captions stay available for scrub-back); only never-in-
            // window tail segments are skipped. Best-effort + off-thread + epoch-guarded exactly as before.
            var starts = new TimeSpan[segments.Count];
            for (int i = 0; i < segments.Count; i++) starts[i] = segments[i].Start;
            var loaded = new bool[segments.Count];
            var merged = new List<TimedCue>();
            var seen = new HashSet<(long, string)>();   // segments repeat boundary-spanning cues — dedupe on (start, text)
            bool sniffed = false, isWebVtt = false;
            int loadedCount = 0;

            while (epoch == _cueEpoch && !_disposed)
            {
                var pos = TimeSpan.FromMilliseconds(_cuePlayheadMs);
                (int lo, int hi) = SubtitleLoader.CueWindow(starts, pos, CueSegmentsBehind, CueSegmentsAhead);
                bool added = false;
                for (int i = lo; i < hi; i++)
                {
                    if (epoch != _cueEpoch || _disposed) return;   // selection superseded / torn down
                    if (loaded[i]) continue;
                    string vtt;
                    try { vtt = await s_subtitleHttp.GetStringAsync(segments[i].Uri).ConfigureAwait(false); }
                    catch (HttpRequestException) { loaded[i] = true; loadedCount++; continue; }   // one missing segment is not fatal
                    loaded[i] = true; loadedCount++;
                    // One-time WebVTT sniff on the FIRST segment actually fetched (windowing means i is not 0-first):
                    // a non-WebVTT rendition (e.g. TTML) stays typed-unsupported — bail without producing garbage cues.
                    if (!sniffed)
                    {
                        sniffed = true;
                        isWebVtt = vtt.AsSpan().TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);
                    }
                    if (!isWebVtt) return;
                    CueTrack part = SubtitleLoader.ParseWebVtt(vtt);
                    for (int c = 0; c < part.Count; c++)
                    {
                        TimedCue cue = part[c];
                        if (seen.Add((cue.Start.Ticks, cue.Text))) { merged.Add(cue); added = true; }
                    }
                }
                // Progressive availability: swap in a freshly-built, start-sorted snapshot (the pump only ever sees a
                // complete, immutable-from-its-view CueTrack — never a list another thread is still mutating).
                if (added && epoch == _cueEpoch) _inbandCues = Snapshot(merged);
                if (loadedCount >= segments.Count) return;   // whole (finite) track loaded — nothing left to window in
                await Task.Delay(CueWindowPollMs).ConfigureAwait(false);   // wait for the playhead to advance the window
            }
        }
        catch
        {
            // Cue loading is best-effort presentation sugar — a failure must never disturb playback.
        }

        static CueTrack Snapshot(List<TimedCue> cues)
        {
            cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));   // window order != time order; CueTrack.Advance scans in time
            var track = new CueTrack();
            for (int i = 0; i < cues.Count; i++) track.Add(cues[i]);
            return track;
        }
    }

    // ── the UI-thread pump (state mapping + the composited-surface handoff) ───────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;

        // ONE seqlock read for this whole turn — wait-free, alloc-free, never torn.
        VideoEngineSnapshot snap = _engine.Snapshot;
        // Stale (the engine hasn't drained our SetSource yet) or superseded (a LATER session reused this warm engine):
        // either way nothing below is safe to act on. The next pump — requested by the engine's own StateChanged once
        // it catches up — retries.
        if (snap.SourceEpoch != _sourceEpoch) return;

        // 1. Terminal error — map the MF media-engine error code to a typed MediaError (published once). Never a silent drop.
        if ((snap.Flags & VideoEngineFlags.Error) != 0)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                sink.Error(MapError(snap.ErrorCode, snap.ErrorHr));
                Publish(sink, PlaybackState.Failed);
            }
            return;
        }

        bool metadataLoaded = (snap.Flags & VideoEngineFlags.MetadataLoaded) != 0;
        bool engineLive = EngineLiveFor(snap);

        // 1b. Still opening — no LOADEDMETADATA yet. Publish from the snapshot's cheap fields so buffering/state/
        // live-timeline don't go dark during the load; there is nothing else to ask (the engine self-refreshes).
        if (!metadataLoaded)
        {
            PlaybackState opening = DeriveState(snap);
            Publish(sink, opening);
            PublishBuffering(sink, opening, snap);
            if (_manifest is { IsLive: true }) PublishLiveTimeline(sink, TimeSpan.FromSeconds(snap.PositionSeconds));
            return;
        }

        // 1c. PRESENTATION EPOCH — MF told us the presentation changed underneath it: FORMATCHANGE (an ABR variant
        // switch, i.e. a NEW decoded frame size), RESOURCELOST (the swap chain went away and is being rebuilt), or the
        // engine just accepted a fresh SetSource (a new source invalidates the old handle just as surely). Drop the
        // cached handle so §3 re-binds, and repaint. The stream size is deliberately NOT reset here: UpdateVideoStream
        // state lives on the media engine (it survives a swapchain rebuild), and §2c re-asserts it whenever the
        // natural size actually changed — so a same-size variant switch never churns the stream rect.
        if (snap.PresentationEpoch != _presentationEpoch)
        {
            _presentationEpoch = snap.PresentationEpoch;
            _handle = 0;
            PublishSurface(sink, default);   // the old swap chain is gone with its presentation; the poster covers until the next handle
            Volatile.Write(ref _repaintPending, 1);
        }

        bool naturalKnown = (snap.Flags & VideoEngineFlags.NaturalSizeKnown) != 0;

        // 2. First metadata → publish natural size / duration / commands and become Ready (or Playing on intent).
        if (!_metaReady)
        {
            _metaReady = true;
            _naturalSize = naturalKnown ? new SizeI((int)snap.NaturalW, (int)snap.NaturalH) : SizeI.Zero;
            sink.NaturalSize(_naturalSize);

            // A DECLARED live source publishes TimeSpan.Zero and never asks the engine: see ForcedLive / the duration
            // note on EngineLiveFor. Publishing zero (rather than skipping the publish) is deliberate — it RESETS
            // whatever a previous source left on the signal.
            double dur = ForcedLive ? 0.0 : snap.DurationSeconds;
            _duration = dur > 0 ? TimeSpan.FromSeconds(dur) : TimeSpan.Zero;
            sink.Duration(_duration);

            PublishCommands(sink, snap, engineLive);

            // Honor the accepted play/pause intent now that the source has resolved.
            _engine.Commands.Post(VideoCommandKind.Transport, a: _playRequested ? 1 : 0);
            Volatile.Write(ref _repaintPending, 1);
        }
        else if (_duration <= TimeSpan.Zero && !ForcedLive && snap.DurationSeconds > 0)
        {
            // 2b. LATE DURATION. The publish above happens once, at first LOADEDMETADATA — and for an adaptive/DASH
            // source GetDuration() is commonly still 0 or non-finite at that instant, so a one-shot publish freezes the
            // length at "unknown" for the whole track. Re-read until it turns positive, then publish once more.
            // Value-gated on _duration, so a finite duration publishes at most twice and a genuinely live/infinite
            // stream never publishes again.
            _duration = TimeSpan.FromSeconds(snap.DurationSeconds);
            sink.Duration(_duration);
        }

        // 2c. LATE / CHANGED NATURAL SIZE — the sibling of 2b, and also the FORMATCHANGE re-publish: re-checked every
        // pump once metadata is ready, but value-gated on the published size, so an unanswered or unchanged size costs
        // one comparison and nothing else.
        if (_metaReady && naturalKnown)
        {
            var answered = new SizeI((int)snap.NaturalW, (int)snap.NaturalH);
            if (answered != _naturalSize)
            {
                _naturalSize = answered;
                sink.NaturalSize(_naturalSize);
                PublishCommands(sink, snap, engineLive);
                _streamW = 0; _streamH = 0;               // the stream is sized FROM the natural size — re-assert it
                Volatile.Write(ref _repaintPending, 1);   // the hand-off below can now report a real surface
            }
        }

        // 3. Composited-surface handoff (Path A) — the single (DRM-free here) bind point. Value-gated all the way down.
        if (binding.IsValid && _metaReady)
        {
            if (_handle == 0)
            {
                _handle = snap.SwapchainHandle;
                if (_handle != 0) Volatile.Write(ref _repaintPending, 1);
            }
            if (_handle != 0)
            {
                binding.Bind(_handle);
                PublishSurface(sink, new VideoSurfaceId(1));   // frames follow the handle at once on the clear path

                // MF renders the FULL decoded frame 1:1 into its own swap chain, and DirectComposition performs the
                // fit — the same contract the protected/PlayReady path has always used. The stream is sized to the
                // natural frame size, capped at what the destination can actually show (below), so a 4K frame in a
                // 640-px card does not allocate 4K buffers. Both numbers move together, so the ratio — the thing the
                // fit depends on — is preserved exactly.
                SizeI content = ContentSizeFor(_naturalSize, videoRect, scale);
                if (content.Width != _streamW || content.Height != _streamH)
                {
                    _engine.Commands.Post(VideoCommandKind.StreamRect, i: content.Width, j: content.Height);
                    _streamW = content.Width; _streamH = content.Height;
                    Volatile.Write(ref _repaintPending, 1);
                }
                binding.SetContentSize(content);
                binding.Place(new RectF(videoRect.X, videoRect.Y, videoRect.W, videoRect.H));
                binding.SetVisible(true);
                // ALWAYS-ON placement report (no env switch): the three numbers that decide letterboxing, published on
                // the media signal so the host log shows the realized geometry instead of leaving it to be inferred
                // from pixels. Value-gated by the signal — an unchanged placement publishes nothing.
                sink.SurfaceGeometry(new VideoSurfaceGeometry(_naturalSize, content,
                    new RectF(videoRect.X, videoRect.Y, videoRect.W, videoRect.H), scale <= 0f ? 1f : scale)
                    { Token = binding.Token });
                if (Interlocked.Exchange(ref _repaintPending, 0) != 0)
                    _engine.Commands.Post(VideoCommandKind.Repaint);
            }
        }

        // 4. State + position from the engine (the presentation clock is authoritative for position; extrapolated
        // forward by elapsed·rate while playing so a pump between two engine self-refreshes still reports a moving
        // playhead instead of a stair-stepped one).
        PlaybackState state = DeriveState(snap);
        Publish(sink, state);
        PublishBuffering(sink, state, snap);
        if (_metaReady)
        {
            double posSeconds = snap.PositionSeconds;
            if ((snap.Flags & VideoEngineFlags.Playing) != 0)
                posSeconds += Stopwatch.GetElapsedTime(snap.PositionTimestamp).TotalSeconds * snap.PlaybackRate;
            if (posSeconds < 0) posSeconds = 0;
            TimeSpan pos = TimeSpan.FromSeconds(posSeconds);
            sink.Position(pos);
            _cuePlayheadMs = (int)Math.Clamp(pos.TotalMilliseconds, 0.0, int.MaxValue);   // feed the windowed cue loader
            TimedCue? cue = null;
            if (_inbandCues is { } cues) { cues.Advance(pos); cue = cues.ActiveCue.Peek(); }
            if (!EqualityComparer<TimedCue?>.Default.Equals(cue, _publishedCue))
            {
                _publishedCue = cue;
                sink.ActiveCue(cue);
            }
        }
        if (_manifest is { IsLive: true }) PublishLiveTimeline(sink, TimeSpan.FromSeconds(snap.PositionSeconds));
        else if (engineLive && _metaReady) PublishEngineLiveTimeline(sink, snap, TimeSpan.FromSeconds(snap.PositionSeconds));
    }

    /// <summary>The manifest-derived command bits (quality/track selection, GoLive), or <c>None</c> for a plain
    /// progressive source. Extracted so the first-metadata publish and the LATE NATURAL SIZE re-publish can never
    /// disagree about what this session can do.</summary>
    private MediaCommandFlags AdaptiveCommands()
    {
        if (_manifest is not { } catalog) return MediaCommandFlags.None;
        MediaCommandFlags adaptive = MediaCommandFlags.SelectVideoQuality;
        for (int g = 0; g < catalog.TrackGroups.Count; g++)
            adaptive |= catalog.TrackGroups[g].Type switch
            {
                AdaptiveTrackType.Audio => MediaCommandFlags.SelectAudioTrack,
                AdaptiveTrackType.Text => MediaCommandFlags.SelectTextTrack,
                _ => MediaCommandFlags.None,
            };
        if (catalog.IsLive) adaptive |= MediaCommandFlags.GoLive;
        return adaptive;
    }

    private static MediaCommandFlags sinkCoreCommands(SizeI size)
        => size.IsEmpty
            ? MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate
            : MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame;

    /// <summary>The stream/content size for a destination — owned by <see cref="VideoStreamSizing.ContentSizeFor"/>
    /// (shared with the protected session so the two backends can never size a surface differently).</summary>
    internal static SizeI ContentSizeFor(SizeI natural, RectF videoRect, float scale)
        => VideoStreamSizing.ContentSizeFor(natural, videoRect, scale);

    private void PublishManifestCatalog(MediaSignalSink sink)
    {
        if (_manifest is not { } manifest) return;
        sink.ResetTracks();
        var qualities = new System.Collections.Generic.List<QualityVariant>();
        int id = 1;
        for (int g = 0; g < manifest.TrackGroups.Count; g++)
        {
            AdaptiveTrackGroup group = manifest.TrackGroups[g];
            if (group.Representations.Count == 0) continue;
            TrackKind kind = group.Type switch
            {
                AdaptiveTrackType.Audio => TrackKind.Audio,
                AdaptiveTrackType.Text => TrackKind.Text,
                _ => TrackKind.Video,
            };
            // Captions default OFF (WinUI/web behavior): only a FORCED text track auto-selects. Audio/video keep the
            // manifest default / first-of-kind rule.
            bool selected = kind == TrackKind.Text
                ? group.IsForced
                : group.IsDefault || !HasSelectedKind(manifest, g, group.Type);
            sink.Track(id, kind, group.Language, LabelOf(group), group.Role,
                group.Representations[0].Quality.Codec, selected);
            if (kind == TrackKind.Text) _textGroups[id] = group;
            id++;
            if (group.Type == AdaptiveTrackType.Video)
                for (int r = 0; r < group.Representations.Count; r++) qualities.Add(group.Representations[r].Quality);
        }
        sink.QualityVariants(qualities);
        sink.QualitySelection(QualitySelection.Auto, qualities.Count > 0 ? qualities[0] : null);
        if (qualities.Count > 0)
        {
            QualityVariant first = qualities[0];
            if (!first.Resolution.IsEmpty)
                sink.VideoGeometry(new VideoGeometry(first.Resolution,
                    new PixelRect(0, 0, first.Resolution.Width, first.Resolution.Height),
                    PixelAspectRatio.Square, 0, first.Resolution));
            sink.VideoColor(first.Hdr switch
            {
                HdrFormat.Hdr10 => new VideoColorInfo(VideoColorPrimaries.Bt2020, VideoTransfer.Pq,
                    VideoMatrix.Bt2020NonConstant, VideoRange.Limited, HdrFormat.Hdr10, null),
                HdrFormat.Hlg => new VideoColorInfo(VideoColorPrimaries.Bt2020, VideoTransfer.Hlg,
                    VideoMatrix.Bt2020NonConstant, VideoRange.Limited, HdrFormat.Hlg, null),
                _ => VideoColorInfo.Sdr,
            });
        }
        PublishLiveTimeline(sink, TimeSpan.Zero);
    }

    private static bool HasSelectedKind(AdaptiveManifest manifest, int before, AdaptiveTrackType type)
    {
        for (int i = 0; i < before; i++)
            if (manifest.TrackGroups[i].Type == type && manifest.TrackGroups[i].Representations.Count > 0) return true;
        return false;
    }

    private static string LabelOf(AdaptiveTrackGroup group)
        => string.IsNullOrWhiteSpace(group.Language) ? group.Id : $"{group.Language} · {group.Id}";

    /// <summary>Publish the command bitset — core transport + the manifest's selection bits + (for an engine-reported
    /// live source) GoLive and the seekability verdict for its DVR window. Value-gated on the last published set, so
    /// the pump can call this every frame: an unchanged bitset publishes nothing.</summary>
    private void PublishCommands(MediaSignalSink sink, in VideoEngineSnapshot snap, bool engineLive)
    {
        MediaCommandFlags flags = sinkCoreCommands(_naturalSize) | AdaptiveCommands();
        if (engineLive) flags = LiveSessionRules.LiveCommands(flags, snap.SeekableStart, snap.SeekableEnd);
        if (flags == _publishedCommands) return;
        _publishedCommands = flags;
        sink.Commands(flags);
    }

    /// <summary>LIVE WITHOUT A MANIFEST: the timeline for a live URL handed straight to Media Foundation (an HLS master
    /// playlist), where the only thing that knows the window is the engine itself — <see cref="PublishLiveTimeline"/>'s
    /// manifest-driven twin. Duration stays <see cref="TimeSpan.Zero"/> (an unbounded source HAS no length, so the seek
    /// bar correctly stays hidden) and the DVR window comes straight off the snapshot, whose end is the live edge. Also
    /// refreshes the commands, because the window's WIDTH is what decides whether seeking is offered and it only
    /// becomes known — and then keeps sliding — after the one-shot first-metadata publish.</summary>
    private void PublishEngineLiveTimeline(MediaSignalSink sink, in VideoEngineSnapshot snap, TimeSpan position)
    {
        PublishCommands(sink, snap, engineLive: true);
        sink.Timeline(LiveSessionRules.Timeline(snap.SeekableStart, snap.SeekableEnd, position.TotalSeconds));
    }

    private void PublishLiveTimeline(MediaSignalSink sink, TimeSpan position)
    {
        if (_manifest is not { } manifest) return;
        TimeSpan start = TimeSpan.MaxValue, end = TimeSpan.Zero;
        for (int g = 0; g < manifest.TrackGroups.Count; g++)
        {
            var reps = manifest.TrackGroups[g].Representations;
            if (reps.Count == 0) continue;
            var segments = reps[0].Segments;
            for (int s = 0; s < segments.Count; s++)
            {
                if (segments[s].Start < start) start = segments[s].Start;
                TimeSpan segmentEnd = segments[s].Start + segments[s].Duration;
                if (segmentEnd > end) end = segmentEnd;
            }
        }
        if (start == TimeSpan.MaxValue) start = TimeSpan.Zero;
        if (!manifest.IsLive && manifest.Duration is { } duration) end = duration;
        TimeSpan liveOffset = manifest.IsLive && end > position ? end - position : TimeSpan.Zero;
        TimeSpan tolerance = manifest.IsLowLatency ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(6);
        sink.Timeline(new TimelineInfo(manifest.IsLive, start, end, end, liveOffset,
            manifest.IsLive && liveOffset <= tolerance, Array.Empty<MediaChapter>()));
    }

    private void PublishBuffering(MediaSignalSink sink, PlaybackState state, in VideoEngineSnapshot snap)
    {
        bool buffering = state is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled;
        if (!buffering)
        {
            _seeking = false;
            sink.Buffering(BufferingInfo.None);
            return;
        }
        BufferPolicy policy = _opts.Buffering ?? BufferPolicy.Vod;
        bool engineSeeking = (snap.Flags & VideoEngineFlags.Seeking) != 0;
        BufferingReason reason = _seeking || engineSeeking ? BufferingReason.Seeking
            : _metaReady ? BufferingReason.Rebuffering : BufferingReason.Initial;
        uint ready = Math.Min(snap.ReadyState, 4u);
        double percent = ready / 4.0;
        TimeSpan target = reason == BufferingReason.Initial ? policy.InitialPlayback : policy.ResumePlayback;
        sink.Buffering(new BufferingInfo(reason, percent, TimeSpan.FromTicks((long)(target.Ticks * percent)), target,
            ready >= 3));
    }

    private PlaybackState DeriveState(in VideoEngineSnapshot snap)
    {
        if ((snap.Flags & VideoEngineFlags.Error) != 0) return PlaybackState.Failed;
        if (!_metaReady) return PlaybackState.Opening;
        if ((snap.Flags & VideoEngineFlags.Ended) != 0) return PlaybackState.Ended;
        // A seek in flight is surfaced as Buffering(Seeking) — MF keeps `Playing` true across a seek, so without this
        // the transport halts with zero feedback until SEEKED lands.
        if ((snap.Flags & VideoEngineFlags.Seeking) != 0) return PlaybackState.Buffering;
        if ((snap.Flags & VideoEngineFlags.Playing) != 0) return PlaybackState.Playing;
        if (_playRequested) return PlaybackState.Buffering;   // intent to play, engine not yet advancing (re-buffering)
        return _everPlayed ? PlaybackState.Paused : PlaybackState.Ready;
    }

    private void Publish(MediaSignalSink sink, PlaybackState state)
    {
        if (state == _publishedState) return;
        _publishedState = state;
        sink.State(state);
    }

    /// <summary>Map an <c>MF_MEDIA_ENGINE_ERR</c> code (+ the raw HRESULT) to a typed <see cref="MediaError"/> — a DRM
    /// shortfall (ENCRYPTED) is a Drm error with <see cref="MediaRecovery.NeedsLicense"/>, never a quiet black frame.
    /// <para>BOTH platform numbers survive the mapping: the <c>MF_MEDIA_ENGINE_ERR</c> value as
    /// <see cref="MediaError.Kind"/> (whose values ARE that vocabulary) and the raw HRESULT as
    /// <see cref="MediaError.UnderlyingCode"/>. A host that reloads live sources needs to tell an expired URL
    /// (NETWORK → re-resolve the locator) from a mid-stream container switch (DECODE → reload the same locator) from
    /// an unplayable format (SOURCE_NOT_SUPPORTED → terminal) — the category alone flattens that policy.</para></summary>
    internal static MediaError MapError(uint mfErr, int hr) => mfErr switch
    {
        // MF_MEDIA_ENGINE_ERR: 1 ABORTED, 2 NETWORK, 3 DECODE, 4 SRC_NOT_SUPPORTED, 5 ENCRYPTED — the same numbering
        // MediaErrorKind carries (it is the HTML5 MediaError.code vocabulary MF implements).
        2 => new MediaError(MediaErrorCategory.Network, "The media download failed.", hr, null, MediaRecovery.NeedsNetwork, MediaErrorKind.Network),
        3 => new MediaError(MediaErrorCategory.Decode, "The media could not be decoded.", hr, null, MediaRecovery.Retryable, MediaErrorKind.Decode),
        4 => new MediaError(MediaErrorCategory.UnsupportedCodec, "The media format is not supported.", hr, null, MediaRecovery.PickLowerQuality, MediaErrorKind.SourceNotSupported),
        5 => new MediaError(MediaErrorCategory.Drm, "The media is encrypted and no license is available.", hr, null, MediaRecovery.NeedsLicense, MediaErrorKind.Encrypted),
        1 => new MediaError(MediaErrorCategory.Source, "Media loading was aborted.", hr, null, MediaRecovery.Retryable, MediaErrorKind.Aborted),
        _ => new MediaError(MediaErrorCategory.Source, "The media source failed.", hr, null, MediaRecovery.Retryable, MediaErrorKind.Unknown),
    };

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _sink = null;
        _engine.StateChanged -= OnEngineStateChanged;
        PumpRequested = null;
        // Non-blocking, no Task.Run: a warm-pooled engine (the production path — MfMediaPlayer.OpenAsync always
        // supplies a release) is returned via a couple of posts (pause + detach), never joined. Only a session built
        // WITHOUT a release callback (tests only — no production caller constructs one this way) falls back to
        // disposing the engine directly.
        if (_release is not null) _release(_engine);
        else _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The pure decisions behind an ENGINE-reported live source (a live URL played by Media Foundation directly, with no
/// parsed manifest): what timeline to publish, which commands the session can offer, and where "go live" lands. Split
/// out of <see cref="MfMediaSession"/> because that class can only run against a real MF engine, while these are the
/// parts that must be pinned by tests — every value here is a threshold someone will otherwise quietly re-tune.
/// </summary>
internal static class LiveSessionRules
{
    /// <summary>How close to the live edge still counts as AT the edge. 6 s is the standard-latency HLS figure (three
    /// 2 s segments): a player that has just resumed sits about one target-duration behind and must not be reported as
    /// late. The low-latency (2 s) figure is manifest-declared and belongs to the manifest path.</summary>
    internal const double LiveEdgeToleranceSeconds = 6.0;

    /// <summary>The narrowest DVR window worth offering a seek bar for. Under this a "seek" is a jitter of a few
    /// seconds that the sliding window invalidates before the user releases the pointer — a control that lies about
    /// what it does. True-live HLS commonly publishes a 3-segment window (about 6 s), so this drops Seek for it and
    /// keeps it for a real DVR window.</summary>
    internal const double MinSeekableWindowSeconds = 30.0;

    /// <summary>How far BEHIND the reported edge "go live" actually lands. The end of the seekable range is the
    /// boundary of the segment MF is still filling; seeking exactly onto it asks for bytes that do not exist yet and
    /// stalls. One segment back is playable immediately and is still "live" by every tolerance above.</summary>
    internal const double GoLiveBackoffSeconds = 3.0;

    /// <summary>The timeline for an engine-live source: the seekable window, its end as the live edge, and how far the
    /// playhead sits behind it. A window that has not been answered yet ((0,0)) still publishes IsLive with a zero
    /// window rather than dropping the live-ness the engine already confirmed.</summary>
    internal static TimelineInfo Timeline(double seekableStart, double seekableEnd, double positionSeconds)
    {
        double start = double.IsFinite(seekableStart) && seekableStart > 0 ? seekableStart : 0;
        double end = double.IsFinite(seekableEnd) && seekableEnd > start ? seekableEnd : start;
        double position = double.IsFinite(positionSeconds) && positionSeconds > 0 ? positionSeconds : 0;
        double offset = end > position ? end - position : 0;
        return new TimelineInfo(
            IsLive: true,
            SeekableStart: TimeSpan.FromSeconds(start),
            SeekableEnd: TimeSpan.FromSeconds(end),
            LiveEdge: TimeSpan.FromSeconds(end),
            LiveOffset: TimeSpan.FromSeconds(offset),
            IsAtLiveEdge: offset <= LiveEdgeToleranceSeconds,
            Chapters: Array.Empty<MediaChapter>());
    }

    /// <summary>The command bitset for an engine-live source: always GoLive (there is an edge to return to), and Seek
    /// only while the DVR window is wide enough to aim inside.</summary>
    internal static MediaCommandFlags LiveCommands(MediaCommandFlags core, double seekableStart, double seekableEnd)
    {
        MediaCommandFlags flags = core | MediaCommandFlags.GoLive;
        double window = seekableEnd - seekableStart;
        if (!double.IsFinite(window) || window < MinSeekableWindowSeconds) flags &= ~MediaCommandFlags.Seek;
        return flags;
    }

    /// <summary>Where a "go live" seek lands: one segment back from the edge, never before the window's start. 0 when
    /// no window has been answered yet — the caller must then not seek at all (a seek to 0 on a live source jumps to
    /// the OLDEST retained bytes, the exact opposite of going live).</summary>
    internal static double GoLiveTarget(double seekableStart, double seekableEnd)
    {
        if (!double.IsFinite(seekableEnd) || seekableEnd <= 0) return 0;
        double start = double.IsFinite(seekableStart) && seekableStart > 0 ? seekableStart : 0;
        double target = seekableEnd - GoLiveBackoffSeconds;
        return target > start ? target : start;
    }
}
