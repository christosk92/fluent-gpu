using System;
using System.Threading;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.WindowsApi.Media.PlayReady;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The VIDEO half of the ONE current media (Milestone B). Implements the app's common IMediaHost seam over the unified
// FluentGpu.Media engine MediaPlayer configured with the MF (+ native PlayReady) backend — this host now OWNS that player
// (the M0 ownership inversion; the builder moved here out of PopOutVideoStage, which only PRESENTS it). The
// PlaybackController swaps its current host to THIS one at a video boundary and drives the
// common transport verbs (Play/Pause/Stop/Seek/SetVolume/PositionMs/IsPlaying) here; the video-specific LoadVideo(source)
// is called at the switch (NOT via IMediaHost). State is polled off the engine's reactive signals and translated into the
// SAME AudioHostSignal channel the audio host emits, so the source-agnostic NowPlayingProjection is unchanged.
//
// MF-PUMP CAVEAT (important): the MediaFoundation video session only ADVANCES while a mounted MediaPlayerElement pumps it
// (IMediaPlayer.PumpVideo, driven from a composited surface's frame loop). This host builds the MediaPlayer and reports
// whatever the session publishes; it does NOT itself pump. A surface (the in-window PiP or the detached pop-out) must be
// mounted and bound to THIS player for frames/position to advance — the video-placement state guarantees one is mounted
// whenever a video is the current media. Surfaces bind to the exact same player instance through CurrentPlayer +
// PlayerChanged (the app mirrors them onto PlaybackBridge.VideoPlayer, a UI-thread signal the surfaces read); EXACTLY ONE
// mounted surface may pump a given player at a time, which the single-placement state guarantees.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The video-media host: the app's common <see cref="IMediaHost"/> seam over a FluentGpu.Media <see cref="MediaPlayer"/>
/// with the MF/PlayReady backend. Zero-alloc-friendly (struct signals, a single reused timer) and fail-soft (every engine
/// call is guarded — a video failure surfaces as an <see cref="AudioHostSignalKind.Error"/>, never a throw across the seam).
/// </summary>
public sealed class FluentVideoMediaHost : IMediaHost
{
    readonly WaveeLogger _log;
    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    readonly Timer _ticker;

    MediaPlayer? _player;
    string _sourceKey = "";           // the PopOutVideoSource.Key the live player was built for ("" = none)
    double _volume = 1.0;
    readonly bool _muted;
    PlaybackState _lastState = PlaybackState.Idle;
    bool _errorReported;
    bool _disposed;

    public FluentVideoMediaHost(WaveeLogger log = default)
    {
        _log = log;
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The live engine player for the current video source (null before the first <see cref="LoadVideo"/> / after a
    /// clear). A mounted video surface (PiP / pop-out) MUST bind to this instance so it pumps the MF session — see the MF-pump
    /// caveat above. Rebuilt on every source change (a clear↔DRM / track switch), announced on <see cref="PlayerChanged"/>.</summary>
    public MediaPlayer? CurrentPlayer { get { lock (_gate) return _player; } }

    /// <summary>The <c>PopOutVideoSource.Key</c> the live <see cref="CurrentPlayer"/> was built for ("" = no player). Used by
    /// <see cref="LoadVideo"/> to make a redundant load of the SAME source a no-op, so re-entering the video path for a track
    /// already playing can never restart it from 0.</summary>
    public string CurrentSourceKey { get { lock (_gate) return _sourceKey; } }

    /// <summary>Fires (on the caller's thread) whenever <see cref="CurrentPlayer"/> is rebuilt/cleared, so a mounted surface
    /// can re-bind to the new player instance. Carries the new player (null after a stop/clear).</summary>
    public event Action<MediaPlayer?>? PlayerChanged;

    // ── IMediaHost common transport (forwarded to the routed MediaPlayer) ────────────────────────────────────────────
    public long PositionMs
    {
        get { var p = CurrentPlayer; return p is null ? 0 : Math.Max(0, (long)p.Position.Peek().TotalMilliseconds); }
    }

    public bool IsPlaying { get { var p = CurrentPlayer; return p is not null && p.IsPlaying.Peek(); } }

    public IObservable<AudioHostSignal> Signals => _signals;

    public void Play()
    {
        var p = CurrentPlayer;
        if (p is not null) { try { _ = p.PlayAsync(); } catch (Exception ex) { _log.Info($"video-host play failed: {ex.Message}"); } }
        StartTicker();
    }

    public void Pause()
    {
        var p = CurrentPlayer;
        if (p is not null) { try { _ = p.PauseAsync(); } catch (Exception ex) { _log.Info($"video-host pause failed: {ex.Message}"); } }
        StopTicker();
    }

    public void Stop()
    {
        StopTicker();
        MediaPlayer? old;
        lock (_gate)
        {
            old = _player;
            _player = null;
            _sourceKey = "";
            _lastState = PlaybackState.Idle;
            _errorReported = false;
        }
        if (old is not null)
        {
            try { old.Stop(); } catch (Exception ex) { _log.Info($"video-host stop failed: {ex.Message}"); }
            _ = DisposePlayerAsync(old);
            PlayerChanged?.Invoke(null);
        }
    }

    public void Seek(long positionMs)
    {
        var p = CurrentPlayer;
        if (p is null) return;
        try { _ = p.SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, positionMs)), SeekMode.Accurate); }
        catch (Exception ex) { _log.Info($"video-host seek failed: {ex.Message}"); }
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        var p = CurrentPlayer;
        if (p is not null) { try { p.SetVolume(_volume); } catch (Exception ex) { _log.Info($"video-host volume failed: {ex.Message}"); } }
    }

    // ── video-specific load (called by the controller at the switch, NOT via IMediaHost) ─────────────────────────────

    /// <summary>Build (or rebuild) the engine <see cref="MediaPlayer"/> for a resolved <see cref="PopOutVideoSource"/> and open
    /// it — the clear MF backend for a Canvas/clear URL, or the clear+DRM backend (native in-process PlayReady CDM) for a DRM
    /// descriptor. THIS HOST OWNS THE PLAYER (the M0 ownership inversion): the surfaces only present <see cref="CurrentPlayer"/>,
    /// so a placement flip re-binds a presenter instead of rebuilding a player — no restart from 0. The prior player (if any)
    /// is torn down first so two sessions never coexist, and a redundant load of the SAME <see cref="PopOutVideoSource.Key"/> is
    /// a no-op for the same reason. Playback advances only once a surface pumps the MF session (see the MF-pump caveat).</summary>
    public void LoadVideo(PopOutVideoSource src)
    {
        if (_disposed || src is null) return;
        // Idempotent for the same source: the controller may re-enter the video path for the track that is already playing
        // (a placement flip, a re-published source, a kind re-evaluation). Rebuilding would restart it from 0 — don't.
        lock (_gate)
        {
            if (_player is not null && string.Equals(_sourceKey, src.Key, StringComparison.Ordinal))
            {
                _log.Info($"video-host load ignored — already playing key={src.Key}");
                return;
            }
        }
        MediaPlayer? old;
        MediaPlayer built;
        try
        {
            built = src.IsDrm
                // MfMediaPlayer routes a DrmConfig-carrying source to the injected DRM backend (native CDM); ProtectedMediaBackend
                // carries the parsed Spotify descriptor (init/segment/stride/PSSH) and the relay POSTs the license challenge.
                ? MediaPlayer.Build()
                    .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer(new ProtectedMediaBackend(src.LicenseRelay!, src.DrmDescriptor!)))
                    .WithDrm(src.LicenseRelay!)
                    .Build()
                : MediaPlayer.Build()
                    .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer())
                    .Build();
        }
        catch (Exception ex)
        {
            _log.Info($"video-host build failed key={src.Key}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, ex.Message));
            return;
        }

        built.SetVolume(_volume);
        built.SetMuted(_muted);

        lock (_gate)
        {
            old = _player;
            _player = built;
            _sourceKey = src.Key ?? "";
            _lastState = PlaybackState.Idle;
            _errorReported = false;
        }
        if (old is not null) _ = DisposePlayerAsync(old);
        // Announce the new player so the mounted surface re-binds its MediaPlayerElement to THIS instance (the app marshals
        // this onto the UI thread; the event fires on the caller's — playback — thread).
        PlayerChanged?.Invoke(built);

        // Open + start (Play == open then play). The descriptor drives the native open for DRM (the URI is advisory); a clear
        // source opens its plain URL. Fire-and-forget — errors surface via the Error signal poll in Tick, never as a throw.
        try
        {
            var source = src.IsDrm
                ? MediaSource.FromUri(src.DrmDescriptor!.InitUrl).With(new DrmConfig(DrmSystem.PlayReady, src.LicenseServerUri))
                : MediaSource.FromUri(src.ClearUrl ?? "");
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
            _ = built.Play(source);
        }
        catch (Exception ex)
        {
            _log.Info($"video-host open failed key={src.Key}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, ex.Message));
            return;
        }
        StartTicker();
        _log.Info($"video-host loaded key={src.Key} drm={src.IsDrm}");
        // A DRM music video plays its OWN soundtrack: the manifest carries an AAC representation under the same content
        // key, and the native CENC source demuxes it alongside the video so Media Foundation renders both under one
        // clock. That is why the song's audio host is stopped while video is the current media — the plain audio track is
        // a DIFFERENT edit (no intro, no spoken pre/post-roll) and would drift against the picture.
        // Logged either way so a silent video is diagnosable: "audio=no" means the manifest offered no AAC representation
        // under the PlayReady index (the parser refuses Opus, which the protected pipeline cannot decode).
        // A clear/Canvas source is unaffected — the MF media engine renders its audio itself.
        if (src.IsDrm)
            _log.Info($"video-host: DRM video is now the current media; the song's audio host is stopped. " +
                $"own-soundtrack={(string.IsNullOrEmpty(src.DrmDescriptor?.AudioInitUrl) ? "NO (video-only manifest)" : "yes " + src.DrmDescriptor!.AudioCodecs)}");
    }

    // ── the poll tick: derive AudioHostSignals from the engine's reactive state (mirrors FluentMediaAudioHost.Tick) ────

    void StartTicker() { if (!_disposed) _ticker.Change(200, 200); }
    void StopTicker() => _ticker.Change(Timeout.Infinite, Timeout.Infinite);

    void Tick()
    {
        if (_disposed) return;
        var p = CurrentPlayer;
        if (p is null) return;

        long pos = PositionMs;

        if (!_errorReported && p.Error.Peek() is { } err)
        {
            _errorReported = true;
            _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, err.Message));
            return;
        }

        var state = p.State.Peek();
        switch (state)
        {
            case PlaybackState.Playing:
                _signals.OnNext(_lastState == PlaybackState.Playing
                    ? new AudioHostSignal(AudioHostSignalKind.PositionTick, pos)
                    : new AudioHostSignal(AudioHostSignalKind.Playing, pos));
                break;
            case PlaybackState.Paused:
            case PlaybackState.Ready:
                if (_lastState is not (PlaybackState.Paused or PlaybackState.Ready))
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, pos));
                break;
            case PlaybackState.Opening:
            case PlaybackState.Buffering:
            case PlaybackState.Stalled:
                if (_lastState != state) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
                break;
            case PlaybackState.Ended:
                if (_lastState != PlaybackState.Ended)
                {
                    StopTicker();
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
                }
                break;
            case PlaybackState.Failed:
                if (!_errorReported)
                {
                    _errorReported = true;
                    _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, "video playback failed"));
                }
                break;
        }
        _lastState = state;
    }

    async System.Threading.Tasks.Task DisposePlayerAsync(MediaPlayer player)
    {
        try { await player.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Info($"video-host dispose failed: {ex.Message}"); }
    }

    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopTicker();
        try { await _ticker.DisposeAsync().ConfigureAwait(false); } catch { }
        MediaPlayer? old;
        lock (_gate) { old = _player; _player = null; _sourceKey = ""; }
        if (old is not null) { try { await old.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
}
