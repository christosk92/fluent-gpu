using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;

namespace FluentGpu.Media.Windows;

/// <summary>
/// A live Media-Foundation video session (spec §9.1): drives an <see cref="IVideoEngine"/> (the PROVEN
/// <see cref="VideoMediaEngine"/> in production) and maps its worker-thread event state onto the player's
/// <see cref="MediaSignalSink"/>. State translation, transport, position projection and the composited-surface handoff all
/// run on the UI/pump thread (<see cref="PumpVideo"/>) so the sink's sole-writer contract holds; every ComPtr stays behind
/// the engine's MTA thread.
/// <list type="bullet">
/// <item>Transport (<see cref="PlayAsync"/>/<see cref="PauseAsync"/>/<see cref="SeekAsync"/>/rate/volume/mute) is
///   idempotent and accepted SYNCHRONOUSLY — it records intent + forwards to the engine and returns a completed task; it
///   never throws or blocks. The next <see cref="PumpVideo"/> realizes the resulting state transition.</item>
/// <item>Position is projected from the engine's presentation clock (authoritative), pushed each pump.</item>
/// <item><see cref="Video"/> is <see cref="VideoDelivery.None"/> until the swap-chain handle + natural size exist, then a
///   <see cref="VideoDelivery.CompositedSurface"/> — the shipping Path A (spec §9.1).</item>
/// </list>
/// </summary>
public sealed class MfMediaSession : IMediaSession, IVideoSurfaceSession
{
    private readonly IVideoEngine _engine;
    private readonly MediaOpenOptions _opts;

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
    private TimeSpan _duration = TimeSpan.Zero;
    private nuint _handle;
    private int _streamW, _streamH;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _errorPublished;

    internal MfMediaSession(IVideoEngine engine, MediaOpenOptions opts)
    {
        _engine = engine;
        _opts = opts;
        _playRequested = !opts.StartPaused;
        if (_playRequested) _everPlayed = true;
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        // Re-apply cold state the engine already accepted, then announce we are opening (metadata pending).
        _engine.SetPlaybackRate(_rate);
        _engine.SetVolume(_volume);
        _engine.SetMuted(_muted);
        sink.PlayRequested(_playRequested);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        // If StartPosition was requested, seek before the first frame (applied once the source resolves too).
        if (_opts.StartPosition > TimeSpan.Zero) _engine.SeekTo(_opts.StartPosition.TotalSeconds);
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _handle != 0 && !_naturalSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _naturalSize, IsHdr: false)
            : VideoDelivery.None;

    // ── transport (idempotent; accepted synchronously; the pump realizes state) ──────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = true;
        _everPlayed = true;
        _engine.Play();
        _sink?.PlayRequested(true);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _engine.Pause();
        _sink?.PlayRequested(false);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return ValueTask.CompletedTask;
        double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
        double t = Math.Clamp(to.TotalSeconds, 0.0, hi);
        _engine.SeekTo(t);
        // Reflect the seek target immediately so a bound seekbar doesn't snap back for a frame.
        _sink?.Position(TimeSpan.FromSeconds(t));
        _sink?.SettleTransport();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void SetRate(double rate) { if (_disposed) return; _rate = rate; _engine.SetPlaybackRate(rate); }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (_disposed) return; _volume = Math.Clamp(volume, 0, 1); _engine.SetVolume(_volume); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (_disposed) return; _muted = muted; _engine.SetMuted(muted); _sink?.Muted(muted); }

    // ── the UI-thread pump (state mapping + the composited-surface handoff) ───────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;

        // 1. Terminal error — map the MF media-engine error code to a typed MediaError (published once). Never a silent drop.
        if (_engine.HasError)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                sink.Error(MapError(_engine.ErrorCode, _engine.ErrorHr));
                Publish(sink, PlaybackState.Failed);
            }
            return;
        }

        // 2. First metadata → publish natural size / duration / commands and become Ready (or Playing on intent).
        if (_engine.MetadataLoaded && !_metaReady)
        {
            _metaReady = true;
            if (_engine.TryGetNativeVideoSize(out uint cx, out uint cy) && cx > 0 && cy > 0)
                _naturalSize = new SizeI((int)cx, (int)cy);
            sink.NaturalSize(_naturalSize);

            double dur = _engine.DurationSeconds;
            _duration = dur > 0 ? TimeSpan.FromSeconds(dur) : TimeSpan.Zero;
            sink.Duration(_duration);

            sink.Commands(_naturalSize.IsEmpty
                ? MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate
                : MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame);

            // Honor the accepted play/pause intent now that the source has resolved.
            if (_playRequested) _engine.Play(); else _engine.Pause();
        }

        // 3. Composited-surface handoff (Path A) — the single (DRM-free here) bind point. Value-gated all the way down.
        if (binding.IsValid && _metaReady)
        {
            if (_handle == 0) _handle = _engine.GetSwapchainHandle();
            if (_handle != 0)
            {
                binding.Bind(_handle);

                int dw = Math.Max(1, (int)MathF.Round(videoRect.W * (scale <= 0 ? 1f : scale)));
                int dh = Math.Max(1, (int)MathF.Round(videoRect.H * (scale <= 0 ? 1f : scale)));
                if (dw != _streamW || dh != _streamH)
                {
                    _engine.SetVideoStreamRect(dw, dh);   // swap-chain-local dst; the presenter clips (does not scale)
                    _streamW = dw; _streamH = dh;
                }
                binding.Place(new RectF(videoRect.X, videoRect.Y, videoRect.W, videoRect.H));
                binding.SetVisible(true);
                _engine.RepaintCurrentFrame();
            }
        }

        // 4. State + position from the engine (the presentation clock is authoritative for position).
        Publish(sink, DeriveState());
        if (_metaReady) sink.Position(TimeSpan.FromSeconds(_engine.CurrentTimeSeconds));
    }

    private PlaybackState DeriveState()
    {
        if (_engine.HasError) return PlaybackState.Failed;
        if (!_metaReady) return PlaybackState.Opening;
        if (_engine.Ended) return PlaybackState.Ended;
        if (_engine.Playing) return PlaybackState.Playing;
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
    /// shortfall (ENCRYPTED) is a Drm error with <see cref="MediaRecovery.NeedsLicense"/>, never a quiet black frame.</summary>
    internal static MediaError MapError(uint mfErr, int hr) => mfErr switch
    {
        // MF_MEDIA_ENGINE_ERR: 1 ABORTED, 2 NETWORK, 3 DECODE, 4 SRC_NOT_SUPPORTED, 5 ENCRYPTED.
        2 => new MediaError(MediaErrorCategory.Network, "The media download failed.", hr, null, MediaRecovery.NeedsNetwork),
        3 => new MediaError(MediaErrorCategory.Decode, "The media could not be decoded.", hr, null, MediaRecovery.Retryable),
        4 => new MediaError(MediaErrorCategory.UnsupportedCodec, "The media format is not supported.", hr, null, MediaRecovery.PickLowerQuality),
        5 => new MediaError(MediaErrorCategory.Drm, "The media is encrypted and no license is available.", hr, null, MediaRecovery.NeedsLicense),
        1 => new MediaError(MediaErrorCategory.Source, "Media loading was aborted.", hr, null, MediaRecovery.Retryable),
        _ => new MediaError(MediaErrorCategory.Source, "The media source failed.", hr, null, MediaRecovery.Retryable),
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sink = null;
        var engine = _engine;
        // The engine tears down its MTA thread + COM on ITS thread (a blocking join); do it off the UI thread.
        await Task.Run(engine.Dispose).ConfigureAwait(false);
    }
}
