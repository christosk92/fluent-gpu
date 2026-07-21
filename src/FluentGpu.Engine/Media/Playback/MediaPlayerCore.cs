using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The shared backing for an <see cref="IMediaPlayer"/> — owns every reactive state signal (the backend is the sole
/// writer), the derived <c>IsPlaying</c>/<c>IsBuffering</c> recompute, the coalescing transport gate (spec principle 12),
/// and the callback-reentrancy firewall (spec §12). Reused by both <see cref="HeadlessScriptedPlayer"/> and the
/// <see cref="MediaPlayer"/> facade so the signal identity + transport semantics are defined in exactly ONE place.
/// <para>Threading: the control thread is the sole writer (spec §12 thread-ownership table). Reads of
/// <see cref="PositionSeconds"/>/<see cref="Position"/> are alloc-free (no boxing — struct signals, no comparer
/// indirection on the hot scalar).</para>
/// </summary>
public sealed class MediaPlayerCore
{
    // ── backing signals (backend is the sole writer) ─────────────────────────────────────────────────────────────────
    private readonly Signal<PlaybackState> _state = new(PlaybackState.Idle);
    private readonly Signal<bool> _playRequested = new(false);
    private readonly Signal<SuppressionReason> _suppression = new(SuppressionReason.None);
    private readonly Signal<bool> _isPlaying = new(false);     // derived, synced by RecomputeDerived (sole-writer contract)
    private readonly Signal<bool> _isBuffering = new(false);   // derived, synced by RecomputeDerived
    private readonly FloatSignal _positionSeconds = new(0f);   // hot scalar
    private readonly Signal<TimeSpan> _position = new(TimeSpan.Zero);
    private readonly Signal<TimeSpan> _duration = new(TimeSpan.Zero);
    private readonly Signal<BufferHealth> _buffer = new(BufferHealth.Empty);
    private readonly Signal<BufferingInfo> _buffering = new(BufferingInfo.None);
    private readonly Signal<TimelineInfo> _timeline = new(TimelineInfo.Empty);
    private readonly Signal<SizeI> _naturalSize = new(SizeI.Zero);
    private readonly Signal<VideoGeometry> _videoGeometry = new(global::FluentGpu.Media.VideoGeometry.Empty);
    private readonly Signal<VideoColorInfo> _videoColor = new(VideoColorInfo.Sdr);
    private readonly Signal<PlaybackStatistics> _statistics = new(PlaybackStatistics.Empty);
    private readonly Signal<TimedCue?> _activeCue = new(null);
    private readonly Signal<MediaError?> _error = new(null);
    private readonly FloatSignal _volume = new(1f);
    private readonly Signal<bool> _muted = new(false);
    private readonly FloatSignal _rate = new(1f);
    private readonly Signal<VideoSurfaceId> _videoSurface = new(default);

    private readonly TransportGate _gate = new();

    // ── callback-reentrancy firewall (spec §12) ──────────────────────────────────────────────────────────────────────
    private int _callbackDepth;
    private readonly List<Action> _deferred = new();

    /// <summary>Create a core with fresh feature objects (tracks/queue/effects/now-playing/commands).</summary>
    public MediaPlayerCore(IAudioEffects? effects = null)
    {
        Effects = effects ?? new AudioEffects();
        Commands = new MediaCommands(MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek
                                     | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame);
    }

    // ── the reactive surface (read-only views + the read-write hot scalars) ──────────────────────────────────────────
    /// <summary>The playback state signal.</summary>
    public IReadSignal<PlaybackState> State => _state;
    /// <summary>The play-intent signal.</summary>
    public IReadSignal<bool> IsPlayRequested => _playRequested;
    /// <summary>The why-not signal.</summary>
    public IReadSignal<SuppressionReason> Suppression => _suppression;
    /// <summary>The derived IsPlaying signal.</summary>
    public IReadSignal<bool> IsPlaying => _isPlaying;
    /// <summary>The derived IsBuffering signal.</summary>
    public IReadSignal<bool> IsBuffering => _isBuffering;
    /// <summary>The hot position-seconds scalar (read-write; the clock writes it).</summary>
    public FloatSignal PositionSeconds => _positionSeconds;
    /// <summary>The TimeSpan position view.</summary>
    public IReadSignal<TimeSpan> Position => _position;
    /// <summary>The duration signal.</summary>
    public IReadSignal<TimeSpan> Duration => _duration;
    /// <summary>The buffer-health signal.</summary>
    public IReadSignal<BufferHealth> Buffer => _buffer;
    /// <summary>The detailed reason/progress behind a buffering state.</summary>
    public IReadSignal<BufferingInfo> Buffering => _buffering;
    /// <summary>The VOD/live/DVR timeline.</summary>
    public IReadSignal<TimelineInfo> Timeline => _timeline;
    /// <summary>The natural-size signal.</summary>
    public IReadSignal<SizeI> NaturalSize => _naturalSize;
    /// <summary>Display geometry including aperture, sample aspect ratio, and rotation.</summary>
    public IReadSignal<VideoGeometry> VideoGeometry => _videoGeometry;
    /// <summary>Colorimetry and HDR metadata.</summary>
    public IReadSignal<VideoColorInfo> VideoColor => _videoColor;
    /// <summary>Bounded-cadence playback diagnostics.</summary>
    public IReadSignal<PlaybackStatistics> Statistics => _statistics;
    /// <summary>The currently-active engine-rendered subtitle/caption cue.</summary>
    public IReadSignal<TimedCue?> ActiveCue => _activeCue;
    /// <summary>The typed-error signal.</summary>
    public IReadSignal<MediaError?> Error => _error;
    /// <summary>The read-write master volume.</summary>
    public FloatSignal Volume => _volume;
    /// <summary>The muted signal.</summary>
    public IReadSignal<bool> Muted => _muted;
    /// <summary>The read-write playback rate.</summary>
    public FloatSignal Rate => _rate;
    /// <summary>The composited-video surface id signal.</summary>
    public IReadSignal<VideoSurfaceId> VideoSurface => _videoSurface;

    /// <summary>The observable track model.</summary>
    public TrackSet Tracks { get; } = new();
    /// <summary>The play queue.</summary>
    public PlayQueue Queue { get; } = new();
    /// <summary>The effects surface.</summary>
    public IAudioEffects Effects { get; }
    /// <summary>The now-playing surface.</summary>
    public NowPlaying NowPlaying { get; } = new();
    /// <summary>The capability bitset.</summary>
    public MediaCommands Commands { get; }
    /// <summary>Adaptive quality variants and acknowledged selection.</summary>
    public QualitySet Qualities { get; } = new();

    // ── setters (value-gated; the sole-writer surface) ───────────────────────────────────────────────────────────────

    /// <summary>Set the playback state (recomputes the derived signals). A recoverable stall never becomes Failed.</summary>
    public void SetState(PlaybackState state)
    {
        _state.Value = state;
        if (state is PlaybackState.Opening or PlaybackState.Buffering)
        {
            if (!_buffering.Peek().IsBuffering)
                _buffering.Value = new BufferingInfo(BufferingReason.Initial, -1, _buffer.Peek().ForwardBuffered,
                    TimeSpan.Zero, false);
        }
        else if (state == PlaybackState.Stalled)
        {
            if (!_buffering.Peek().IsBuffering)
                _buffering.Value = new BufferingInfo(BufferingReason.Rebuffering, -1,
                    _buffer.Peek().ForwardBuffered, TimeSpan.Zero, false);
        }
        else if (_buffering.Peek().IsBuffering)
        {
            _buffering.Value = BufferingInfo.None;
        }
        RecomputeDerived();
    }

    /// <summary>Set the play-intent signal (recomputes derived).</summary>
    public void SetPlayRequested(bool requested)
    {
        _playRequested.Value = requested;
        RecomputeDerived();
    }

    /// <summary>Set the suppression reason (recomputes derived).</summary>
    public void SetSuppression(SuppressionReason reason)
    {
        _suppression.Value = reason;
        RecomputeDerived();
    }

    /// <summary>Set the position from the AUTHORITATIVE tick-domain value (spec §2 / §7.6): <see cref="Position"/> stays
    /// exact 100-ns, and the hot <see cref="PositionSeconds"/> scalar is a LOSSY one-way projection FROM it (never the
    /// reverse) — so seek/duration math stays exact for multi-hour content while the float stays the cheap node-bindable
    /// scrub value. Value-gated.</summary>
    public void SetPosition(TimeSpan position)
    {
        _position.Value = position;                        // authoritative, exact
        _positionSeconds.Value = (float)position.TotalSeconds;   // lossy UI projection
        if (NowPlaying.Enabled)
            NowPlaying.SetPositionState(position, _duration.Peek(), _state.Peek() == PlaybackState.Playing ? _rate.Peek() : 0.0);
    }

    /// <summary>Set the media duration (TimeSpan.MinValue == unknown/live).</summary>
    public void SetDuration(TimeSpan duration) => _duration.Value = duration;
    /// <summary>Set the buffer health.</summary>
    public void SetBuffer(BufferHealth buffer) => _buffer.Value = buffer;
    public void SetBuffering(BufferingInfo buffering) => _buffering.Value = buffering;
    public void SetTimeline(TimelineInfo timeline) => _timeline.Value = timeline;
    /// <summary>Set the video natural size.</summary>
    public void SetNaturalSize(SizeI size) => _naturalSize.Value = size;
    public void SetVideoGeometry(VideoGeometry geometry)
    {
        _videoGeometry.Value = geometry;
        SizeI display = geometry.DisplaySize.IsEmpty ? geometry.CodedSize : geometry.DisplaySize;
        if (!display.IsEmpty) _naturalSize.Value = display;
    }
    public void SetVideoColor(VideoColorInfo color) => _videoColor.Value = color;
    public void SetStatistics(PlaybackStatistics statistics) => _statistics.Value = statistics;
    public void SetActiveCue(TimedCue? cue) => _activeCue.Value = cue;
    /// <summary>Set (or clear with null) the typed error.</summary>
    public void SetError(MediaError? error) => _error.Value = error;
    /// <summary>Set the muted state.</summary>
    public void SetMuted(bool muted) => _muted.Value = muted;
    /// <summary>Set the composited-video surface id.</summary>
    public void SetVideoSurface(VideoSurfaceId id) => _videoSurface.Value = id;
    /// <summary>Set the available-commands bitset.</summary>
    public void SetCommands(MediaCommandFlags flags) => Commands.Set(flags);

    private void RecomputeDerived()
    {
        var s = _state.Peek();
        _isPlaying.Value = s == PlaybackState.Playing && _suppression.Peek() == SuppressionReason.None;
        _isBuffering.Value = s is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled;
    }

    // ── coalescing transport gate (spec principle 12) ────────────────────────────────────────────────────────────────

    /// <summary>Begin a transport op: the previously-pending op COMPLETES (never throws) — supersession — and a fresh
    /// gate <see cref="ValueTask"/> is returned that completes on the next <see cref="SettleTransport"/>. If invoked
    /// reentrantly from inside a source/feed callback, <paramref name="apply"/> is DEFERRED (run after the callback) and
    /// a completed task is returned — non-reentrant by construction.</summary>
    public ValueTask BeginTransport(Action apply)
    {
        if (_callbackDepth > 0)
        {
            // Reentrant callback (spec §12): never mutate player state inside a firewalled callback — defer + complete.
            _deferred.Add(apply);
            return ValueTask.CompletedTask;
        }
        apply();
        return _gate.Begin();
    }

    /// <summary>Record a transport intent for a deterministic, externally-pumped owner. The intent is applied now (or
    /// deferred until a firewalled callback exits), but no completion gate is created: accepting the command is the
    /// synchronous operation and the owner's pump realizes the resulting state transition later.</summary>
    internal void RecordTransportIntent(Action apply)
    {
        if (_callbackDepth > 0)
        {
            _deferred.Add(apply);
            return;
        }

        apply();
    }

    /// <summary>Complete the currently-pending transport op (called by the owner when the intent is realized).</summary>
    public void SettleTransport() => _gate.SettleAll();

    // ── the callback firewall scope ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter a firewalled-callback scope (a byte-source/feed <c>Read</c>/fill callback). Transport verbs invoked
    /// while inside are deferred (safe/non-reentrant); the deferred intents flush when the scope disposes. Reentrant-safe
    /// (nests).</summary>
    public CallbackScope EnterCallback() => new(this);

    /// <summary>A firewalled-callback scope (see <see cref="EnterCallback"/>).</summary>
    public readonly struct CallbackScope : IDisposable
    {
        private readonly MediaPlayerCore _core;
        internal CallbackScope(MediaPlayerCore core) { _core = core; core._callbackDepth++; }
        /// <summary>Exit the scope — flush deferred transport intents once the outermost scope closes.</summary>
        public void Dispose()
        {
            if (--_core._callbackDepth == 0 && _core._deferred.Count > 0)
            {
                // Copy-drain so a deferred action that re-enters cannot mutate the list under iteration.
                var pending = _core._deferred.ToArray();
                _core._deferred.Clear();
                foreach (var a in pending) a();
                _core.SettleTransport();
            }
        }
    }
}

/// <summary>A tiny single-thread coalescing gate: each <see cref="Begin"/> completes the prior pending op (supersession,
/// never a throw) and returns a fresh <see cref="ValueTask"/> that completes on <see cref="SettleAll"/>.</summary>
internal sealed class TransportGate
{
    private TaskCompletionSource? _current;

    public ValueTask Begin()
    {
        _current?.TrySetResult();   // supersede the prior in-flight op — complete, never throw
        _current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new ValueTask(_current.Task);
    }

    public void SettleAll()
    {
        _current?.TrySetResult();
        _current = null;
    }
}

/// <summary>
/// The backend → engine signal-write channel (spec §4.4 <c>IMediaSession.ConnectSignals</c>). A backend/session pushes
/// state through this (marshaled to the safe context); it forwards value-gated to the owning <see cref="MediaPlayerCore"/>.
/// Keeps the backend from touching signal internals directly and gives the facade one place to observe a session.
/// </summary>
public sealed class MediaSignalSink
{
    private readonly MediaPlayerCore _core;

    /// <summary>Create a sink over <paramref name="core"/>.</summary>
    public MediaSignalSink(MediaPlayerCore core) => _core = core;

    /// <summary>Push the playback state.</summary>
    public void State(PlaybackState state) => _core.SetState(state);
    /// <summary>Push the play-intent.</summary>
    public void PlayRequested(bool requested) => _core.SetPlayRequested(requested);
    /// <summary>Push the suppression reason.</summary>
    public void Suppression(SuppressionReason reason) => _core.SetSuppression(reason);
    /// <summary>Push the position from the authoritative tick-domain value (exact; the float is projected from it).</summary>
    public void Position(TimeSpan position) => _core.SetPosition(position);
    /// <summary>Push the duration.</summary>
    public void Duration(TimeSpan duration) => _core.SetDuration(duration);
    /// <summary>Push the buffer health.</summary>
    public void Buffer(BufferHealth buffer) => _core.SetBuffer(buffer);
    /// <summary>Push detailed buffering progress/reason.</summary>
    public void Buffering(BufferingInfo buffering) => _core.SetBuffering(buffering);
    /// <summary>Push the current live/VOD timeline.</summary>
    public void Timeline(TimelineInfo timeline) => _core.SetTimeline(timeline);
    /// <summary>Push the natural size.</summary>
    public void NaturalSize(SizeI size) => _core.SetNaturalSize(size);
    /// <summary>Push display geometry.</summary>
    public void VideoGeometry(VideoGeometry geometry) => _core.SetVideoGeometry(geometry);
    /// <summary>Push colorimetry/HDR metadata.</summary>
    public void VideoColor(VideoColorInfo color) => _core.SetVideoColor(color);
    /// <summary>Push bounded-cadence playback statistics.</summary>
    public void Statistics(PlaybackStatistics statistics) => _core.SetStatistics(statistics);
    /// <summary>Replace the available quality variants.</summary>
    public void QualityVariants(IEnumerable<QualityVariant> variants) => _core.Qualities.Variants.Reset(variants);
    /// <summary>Publish the acknowledged quality selection and active representation.</summary>
    public void QualitySelection(QualitySelection selection, QualityVariant? active)
    {
        _core.Qualities.PublishSelection(selection);
        _core.Qualities.PublishActive(active);
    }
    /// <summary>Reset tracks before publishing a new source's manifest/backend discovery.</summary>
    public void ResetTracks() => _core.Tracks.Reset();
    /// <summary>Publish one backend-discovered selectable track.</summary>
    public MediaTrack Track(int id, TrackKind kind, string? language, string label, TrackRole role,
        MediaContentType codec, bool selected = false)
        => _core.Tracks.Register(id, kind, language, label, role, codec, selected);
    /// <summary>Push the active engine-rendered subtitle/caption cue (null = none). Used by backends that source cues
    /// from the stream/manifest (in-band WebVTT); external sidecar subtitles are advanced by the player facade.</summary>
    public void ActiveCue(TimedCue? cue) => _core.SetActiveCue(cue);
    /// <summary>Push (or clear) the typed error.</summary>
    public void Error(MediaError? error) => _core.SetError(error);
    /// <summary>Push the muted state.</summary>
    public void Muted(bool muted) => _core.SetMuted(muted);
    /// <summary>Push the composited-video surface id.</summary>
    public void VideoSurface(VideoSurfaceId id) => _core.SetVideoSurface(id);
    /// <summary>Push the available-commands bitset.</summary>
    public void Commands(MediaCommandFlags flags) => _core.SetCommands(flags);
    /// <summary>Complete the currently-pending transport op (the intent was realized).</summary>
    public void SettleTransport() => _core.SettleTransport();
}
