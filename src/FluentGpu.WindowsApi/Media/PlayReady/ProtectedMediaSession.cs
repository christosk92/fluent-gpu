using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
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
public sealed class ProtectedMediaSession : IMediaSession, IVideoSurfaceSession
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

    /// <summary>Create a protected session over <paramref name="player"/> for <paramref name="request"/>.</summary>
    public ProtectedMediaSession(IProtectedVideoPlayer player, ProtectedVideoRequest request, MediaOpenOptions opts)
    {
        _player = player;
        _request = request;
        _opts = opts;
        _playRequested = !opts.StartPaused;
        _locus = new MediaLocus(null, request.Source, null, null, null);
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        sink.PlayRequested(!_opts.StartPaused);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        StartOnce();
    }

    private void StartOnce()
    {
        if (_started) return;
        _started = true;
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
        return _player.PlayAsync();
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _sink?.PlayRequested(false);
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
    }

    /// <inheritdoc/>
    public void SetRate(double rate) { if (!_disposed) _player.SetRate((float)rate); }
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

    // ── the UI-thread pump (state mapping + the composited-surface handoff) ───────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;

        // Advance the native snapshot + bind the PROTECTED DComp handle (value-gated inside the player).
        _player.Pump(binding);
        var pv = _player.State.Value;

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
                sink.Commands(MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate);
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
        var player = _player;
        return new ValueTask(Task.Run(() =>
        {
            try { player.Stop(); } catch { }
            player.Dispose();
        }));
    }
}
