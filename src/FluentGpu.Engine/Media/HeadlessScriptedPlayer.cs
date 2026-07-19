using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// A REAL headless <see cref="IMediaPlayer"/> (spec M0 deliverable 6) driven by a scripted <see cref="IMediaSampleSource"/>
/// and a supplied VIRTUAL clock (<see cref="Pump"/>). It genuinely advances <see cref="PlaybackState"/> through
/// Idle→Opening→Buffering→Ready→Playing→Paused/Stalled/Ended/Failed, advances <see cref="PositionSeconds"/> off the
/// virtual clock, accepts idempotent transport intents synchronously while <see cref="Pump"/> realizes them, enforces
/// the callback-reentrancy firewall (spec §12), and surfaces typed <see cref="MediaError"/>. No GPU, no backend — this
/// is what the deterministic engine unit tests drive. It is genuinely functional, not a mock.
/// <para>The demuxer (the sample source) is authoritative for END-OF-STREAM (a <c>null</c> sample → Ended); the virtual
/// clock is authoritative for POSITION (spec §2 "the device clock is the only clock"). Stalls and failures are injected
/// deterministically via <see cref="InjectStall"/>/<see cref="Resume"/>/<see cref="InjectFailure"/>.</para>
/// </summary>
public sealed class HeadlessScriptedPlayer : IMediaPlayer
{
    private readonly MediaPlayerCore _core = new();

    private IMediaSampleSource? _source;
    private TimeSpan _clock;            // AUTHORITATIVE position (tick domain — exact for multi-hour content)
    private TimeSpan _duration;
    private int _openTicksRemaining;
    private int _bufferTicksRemaining;
    private bool _stalled;
    private bool _disposed;
    private long _prepareId;
    private uint _prepareEpoch;
    private TimeSpan? _pendingSeek;
    private double _sampleCursorPts;   // demuxer read cursor (PTS of the next sample to pull; the demuxer's own domain)

    /// <summary>Pumps spent in Opening before metadata is published (Idle→Opening→Buffering). Default 1.</summary>
    public int OpenTicks { get; init; } = 1;
    /// <summary>Pumps spent in Buffering before Ready. Default 1.</summary>
    public int BufferTicks { get; init; } = 1;
    /// <summary>The synthesized duration used when a source is not a sample source. Default 10 s.</summary>
    public TimeSpan DefaultDuration { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The sample duration the synthesized demuxer emits per pull. Default 20 ms.</summary>
    public TimeSpan SampleDuration { get; init; } = TimeSpan.FromMilliseconds(20);

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
    public IReadSignal<SizeI> NaturalSize => _core.NaturalSize;
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
    public IReadSignal<VideoSurfaceId> VideoSurface => _core.VideoSurface;

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, FluentGpu.Foundation.RectF videoRect, float scale) { /* no composited surface headlessly */ }

    /// <summary>The underlying core (for tests/hosts that need the sink or setters).</summary>
    public MediaPlayerCore Core => _core;

    // ── transport (idempotent intents; acceptance completes synchronously, Pump realizes state) ─────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.RecordTransportIntent(() => _core.SetPlayRequested(true));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.RecordTransportIntent(() =>
        {
            _core.SetPlayRequested(false);
            if (_core.State.Peek() == PlaybackState.Playing) _core.SetState(PlaybackState.Paused);
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_disposed) return;
        _core.SetPlayRequested(false);
        _stalled = false;
        _clock = TimeSpan.Zero;
        _sampleCursorPts = 0;
        _core.SetPosition(TimeSpan.Zero);
        _core.SetSuppression(SuppressionReason.None);
        _core.SetState(PlaybackState.Idle);
        _core.SettleTransport();
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode = SeekMode.Accurate)
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.RecordTransportIntent(() =>
        {
            // A Seek INVALIDATES + re-prepares the prepared slot — bump the epoch so a stale late Prepare is dropped
            // (the crossfade-prepared-next scar, spec §8.4). Seek is in the TICK domain — no float round-trip.
            _prepareEpoch++;
            var hi = _duration > TimeSpan.Zero ? _duration : to;
            _pendingSeek = to < TimeSpan.Zero ? TimeSpan.Zero : (to > hi ? hi : to);
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask StepFrame(int delta)
    {
        if (_disposed) return ValueTask.CompletedTask;
        var target = _clock + SampleDuration * delta;
        return SeekAsync(target < TimeSpan.Zero ? TimeSpan.Zero : target, SeekMode.Accurate);
    }

    /// <inheritdoc/>
    public void SetRate(double rate) { if (!_disposed) _core.Rate.Value = (float)rate; }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (!_disposed) _core.Volume.Value = (float)Math.Clamp(volume, 0, 1); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (!_disposed) _core.SetMuted(muted); }

    // ── source + queue + preroll ─────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask OpenAsync(MediaSource source, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (ct.IsCancellationRequested) return ValueTask.FromCanceled(ct);

        _source = ResolveSampleSource(source);
        _stalled = false;
        _clock = TimeSpan.Zero;
        _sampleCursorPts = 0;
        _pendingSeek = null;
        _core.SetError(null);
        _core.SetPosition(TimeSpan.Zero);
        _core.SetSuppression(SuppressionReason.None);
        _core.Tracks.Text.Clear();
        _core.Tracks.Audio.Clear();
        _core.Tracks.Video.Clear();

        // Metadata → NowPlaying (seeded without a round-trip when the source carries it, spec §5).
        if (source.Metadata is { } meta) _core.NowPlaying.Metadata = meta;

        _openTicksRemaining = Math.Max(0, OpenTicks);
        _bufferTicksRemaining = Math.Max(0, BufferTicks);
        _core.SetState(PlaybackState.Opening);

        // Opening is virtual-clock driven. Completion means the command was accepted; Pump advances
        // Opening -> Buffering -> Ready deterministically on the caller's schedule.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Enqueue(MediaSource next) { if (!_disposed) _core.Queue.Add(next); }

    /// <inheritdoc/>
    public PrepareToken PrepareNext(MediaSource next)
    {
        if (_disposed) return PrepareToken.None;
        _core.Queue.Add(next);
        return new PrepareToken(++_prepareId, _prepareEpoch);
    }

    // ── the virtual-clock driver (harness/host calls this to advance) ────────────────────────────────────────────────

    /// <summary>Advance the virtual clock by <paramref name="dt"/> and step the state machine one tick. Returns the new
    /// state. This is the deterministic "pull N frames" harness op — the headless parity of the RT/clock tick.</summary>
    public PlaybackState Pump(TimeSpan dt)
    {
        if (_disposed) return _core.State.Peek();

        // Apply a pending seek atomically at the tick boundary.
        if (_pendingSeek is { } seek)
        {
            _clock = seek;
            _sampleCursorPts = seek.TotalSeconds;
            _pendingSeek = null;
            _core.SetPosition(_clock);
            _source?.SeekAsync(_clock, default);
            if (_core.State.Peek() == PlaybackState.Ended && _clock < _duration)
                _core.SetState(_core.IsPlayRequested.Peek() ? PlaybackState.Ready : PlaybackState.Paused);
        }

        switch (_core.State.Peek())
        {
            case PlaybackState.Opening:
                if (_openTicksRemaining-- <= 0)
                    EnterBuffering();
                break;

            case PlaybackState.Buffering:
                if (_bufferTicksRemaining-- <= 0)
                {
                    _core.SetBuffer(new BufferHealth(Array.Empty<TimeRange>(), _duration < TimeSpan.FromSeconds(30) ? _duration : TimeSpan.FromSeconds(30), false, StallPolicy.Rebuffer));
                    _core.SetState(PlaybackState.Ready);
                    if (_core.IsPlayRequested.Peek()) _core.SetState(PlaybackState.Playing);
                }
                break;

            case PlaybackState.Ready:
            case PlaybackState.Paused:
                if (_core.IsPlayRequested.Peek() && !_stalled) _core.SetState(PlaybackState.Playing);
                break;

            case PlaybackState.Stalled:
                if (!_stalled)   // Resume() cleared the underrun → refill and continue
                {
                    _core.SetSuppression(SuppressionReason.None);
                    _core.SetState(_core.IsPlayRequested.Peek() ? PlaybackState.Playing : PlaybackState.Paused);
                }
                break;

            case PlaybackState.Playing:
                AdvancePlaying(dt);
                break;

            case PlaybackState.Ended:
                // A play request after Ended restarts from 0.
                if (_core.IsPlayRequested.Peek())
                {
                    _clock = TimeSpan.Zero; _sampleCursorPts = 0;
                    _core.SetPosition(TimeSpan.Zero);
                    _core.SetState(PlaybackState.Playing);
                }
                break;
        }

        // Also settle a core-level gate if a host/test drove the exposed Core directly.
        _core.SettleTransport();
        return _core.State.Peek();
    }

    private void AdvancePlaying(TimeSpan dt)
    {
        if (_stalled)
        {
            _core.SetSuppression(SuppressionReason.BufferingUnderrun);
            _core.SetState(PlaybackState.Stalled);
            return;
        }

        double rate = Math.Max(0.0, _core.Rate.Peek());
        _clock += dt * rate;   // tick-domain advance (TimeSpan × double) — exact, no float accumulation
        if (_duration > TimeSpan.Zero && _clock > _duration) _clock = _duration;
        _core.SetPosition(_clock);

        // Drive END-OF-STREAM off the demuxer: pull samples up to the clock; a null sample ⇒ EOS.
        if (PullSamplesUpTo(_clock.TotalSeconds))
        {
            _core.SetPlayRequested(false);
            _core.SetState(PlaybackState.Ended);
        }
    }

    private void EnterBuffering()
    {
        // Publish metadata from the demuxer's typed streams (spec §5.3).
        _duration = DefaultDuration;
        var size = SizeI.Zero;
        if (_source is { } src)
        {
            var streams = src.Streams;
            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                if (s.Duration > TimeSpan.Zero) _duration = s.Duration;
                if (s.Kind == StreamKind.Video && !s.NaturalSize.IsEmpty) size = s.NaturalSize;
                var kind = s.Kind switch { StreamKind.Video => TrackKind.Video, StreamKind.Text => TrackKind.Text, _ => TrackKind.Audio };
                _core.Tracks.Register(s.Index, kind, s.Language, s.Language ?? kind.ToString(), s.Role, s.Codec, selected: true);
            }
        }
        _core.SetDuration(_duration);
        _core.SetNaturalSize(size);
        _core.SetCommands(size.IsEmpty
            ? MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.Next | MediaCommandFlags.Previous
            : MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame | MediaCommandFlags.PictureInPicture);
        _core.SetState(PlaybackState.Buffering);
    }

    // Pull the demuxer forward to <paramref name="seconds"/>; returns true when EOS (a null sample) is reached. The
    // demuxer is authoritative for EOS: we pull until the cursor passes the clock, and when the clock has reached the
    // (demuxer-reported) duration we confirm EOS with one more pull (a null sample ⇒ Ended).
    private bool PullSamplesUpTo(double seconds)
    {
        double durationSeconds = _duration.TotalSeconds;
        if (_source is null) return durationSeconds > 0 && seconds >= durationSeconds;
        int guard = 0;
        while (_sampleCursorPts < seconds && guard++ < 4096)
        {
            var sample = PullOne();
            if (sample is null) return true;   // EOS from the demuxer
            _sampleCursorPts = sample.Value.Pts.TotalSeconds + (sample.Value.Duration?.TotalSeconds ?? SampleDuration.TotalSeconds);
        }
        if (durationSeconds > 0 && seconds >= durationSeconds)
        {
            var tail = PullOne();
            if (tail is null) return true;
            _sampleCursorPts = tail.Value.Pts.TotalSeconds + (tail.Value.Duration?.TotalSeconds ?? SampleDuration.TotalSeconds);
        }
        return false;
    }

    private MediaSample? PullOne()
    {
        var vt = _source!.GetSampleAsync(0, default);
        return vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
    }

    private IMediaSampleSource ResolveSampleSource(MediaSource source)
        => source switch
        {
            SampleSource ss => ss.Source,
            _ => new ScriptedSampleSource(DefaultDuration, SampleDuration)
        };

    // ── deterministic event injection (the scripted timeline) ────────────────────────────────────────────────────────

    /// <summary>Inject a buffer underrun: the next pump while playing enters <see cref="PlaybackState.Stalled"/> with
    /// <see cref="SuppressionReason.BufferingUnderrun"/> (transient — never becomes Failed).</summary>
    public void InjectStall() => _stalled = true;

    /// <summary>Clear an injected underrun; the next pump resumes (Stalled→Playing/Paused).</summary>
    public void Resume() => _stalled = false;

    /// <summary>Inject a terminal typed failure (→ <see cref="PlaybackState.Failed"/> + the <c>Error</c> signal).</summary>
    public void InjectFailure(MediaError error)
    {
        if (_disposed) return;
        _core.SetError(error);
        _core.SetPlayRequested(false);
        _core.SetState(PlaybackState.Failed);
    }

    /// <summary>Run <paramref name="reenter"/> inside the firewalled-callback scope (models a byte-source/feed <c>Read</c>
    /// callback on the media-IO thread). Transport verbs invoked from within are deferred + non-reentrant — safe (spec
    /// §12). The gate uses this to assert reentrancy safety.</summary>
    public void RunSourceCallback(Action reenter)
    {
        using (_core.EnterCallback())
            reenter();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _core.SettleTransport();           // any pending transport op completes (never throws)
        _source = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A scripted <see cref="IMediaSampleSource"/> (spec M0 deliverable 6) — a synchronous, deterministic demuxer that emits
/// audio (and optionally video) samples with monotonically-increasing PTS until <see cref="Duration"/>, then <c>null</c>
/// (EOS). Drives <see cref="HeadlessScriptedPlayer"/> headlessly with no I/O.
/// </summary>
public sealed class ScriptedSampleSource : IMediaSampleSource
{
    private readonly TimeSpan _sampleDuration;
    private readonly StreamDescriptor[] _streams;
    private double _cursorSeconds;
    private readonly double _durationSeconds;

    /// <summary>An audio-only scripted source of the given duration.</summary>
    public ScriptedSampleSource(TimeSpan duration, TimeSpan sampleDuration, SizeI? video = null)
    {
        _durationSeconds = duration.TotalSeconds;
        _sampleDuration = sampleDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(20) : sampleDuration;
        var streams = new List<StreamDescriptor>
        {
            new(0, StreamKind.Audio, new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis), duration, SizeI.Zero, "und", TrackRole.Main)
        };
        if (video is { } v && !v.IsEmpty)
            streams.Add(new StreamDescriptor(1, StreamKind.Video, new MediaContentType(Container.Mp4, CodecId.H264, CodecId.Aac), duration, v, "und", TrackRole.Main));
        _streams = streams.ToArray();
    }

    /// <inheritdoc/>
    public IReadOnlyList<StreamDescriptor> Streams => _streams;
    /// <inheritdoc/>
    public DrmConfig? Drm => null;

    /// <inheritdoc/>
    public ValueTask<MediaSample?> GetSampleAsync(int streamIndex, CancellationToken ct)
    {
        if (_cursorSeconds >= _durationSeconds) return new ValueTask<MediaSample?>((MediaSample?)null);
        var pts = TimeSpan.FromSeconds(_cursorSeconds);
        _cursorSeconds += _sampleDuration.TotalSeconds;
        var sample = new MediaSample(streamIndex, ReadOnlyMemory<byte>.Empty, pts, _sampleDuration, IsKeyframe: true, SampleFlags.None);
        return new ValueTask<MediaSample?>(sample);
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, CancellationToken ct)
    {
        _cursorSeconds = Math.Clamp(to.TotalSeconds, 0, _durationSeconds);
        return ValueTask.CompletedTask;
    }
}
