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
/// signals through a shared <see cref="MediaPlayerCore"/>, and swaps the inner backend only when the source
/// <see cref="MediaKind"/> changes (spec §12 reuse-vs-recreate). <c>Play(source)</c> is the whole 90% case.
/// <para>M0: the concrete video/audio backends land in M1/M2 — until one is registered on the <see cref="MediaRouter"/>,
/// <see cref="OpenAsync"/> surfaces an honest <see cref="MediaError.NoBackend"/> rather than pretending. The routing +
/// signal-forwarding + backend-swap wiring is fully real and exercised headlessly via a registered test backend.</para>
/// </summary>
public sealed class MediaPlayer : IMediaPlayer, IAsyncDisposable
{
    private readonly MediaPlayerCore _core = new();
    private readonly MediaSignalSink _sink;
    private readonly MediaRouter _router;
    private readonly NetworkOptions? _network;
    private readonly BufferPolicy? _buffering;
    private readonly IAbrPolicy? _abr;
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _licenseRelay;

    private IMediaSession? _session;
    private MediaKind _currentKind = MediaKind.Auto;
    private bool _disposed;

    internal MediaPlayer(MediaRouter router, NetworkOptions? network, BufferPolicy? buffering,
                         IAbrPolicy? abr, Func<LicenseRequest, ValueTask<LicenseResponse>>? licenseRelay)
    {
        _router = router;
        _network = network;
        _buffering = buffering;
        _abr = abr;
        _licenseRelay = licenseRelay;
        _sink = new MediaSignalSink(_core);
    }

    /// <summary>Create a player with working defaults; the backend is auto-selected on the first <c>Play</c>.</summary>
    public static MediaPlayer Create() => new(new MediaRouter(), null, null, null, null);
    /// <summary>Start the Layer-2 power path (spec §4.1).</summary>
    public static MediaPlayerBuilder Build() => new();

    /// <summary>The underlying core (for the SMTC bridge and other consumers of the same headless signals).</summary>
    public MediaPlayerCore Core => _core;
    /// <summary>The current routed session (null before the first successful open).</summary>
    public IMediaSession? Session => _session;

    // ── the one-call easy path (all funnel into Play(MediaSource)) ───────────────────────────────────────────────────

    /// <summary>Play a URI or local path (auto buffering / SMTC / default tracks).</summary>
    public ValueTask Play(string uriOrPath)
        => Play(LooksLikeUri(uriOrPath) ? MediaSource.FromUri(uriOrPath, _network) : MediaSource.FromFile(uriOrPath));
    /// <summary>Play a stream.</summary>
    public ValueTask Play(Stream stream) => Play(MediaSource.FromStream(stream));
    /// <summary>Play in-memory bytes.</summary>
    public ValueTask Play(ReadOnlyMemory<byte> bytes) => Play(MediaSource.FromBytes(bytes));
    /// <summary>The general form all overloads funnel into: open then play.</summary>
    public async ValueTask Play(MediaSource source)
    {
        await OpenAsync(source).ConfigureAwait(false);
        if (_core.Error.Peek() is null) await PlayAsync().ConfigureAwait(false);
    }

    private static bool LooksLikeUri(string s) => s.Contains("://", StringComparison.Ordinal);

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
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed) return;
        // Only a composited-video session (the MF backend) drives the surface handoff; everything else is a no-op.
        (_session as IVideoSurfaceSession)?.PumpVideo(binding, videoRect, scale);

        // Tell the host whether this video needs the frame loop kept awake at display rate. "Presenting" means the user
        // intends playback AND the session is either advancing (Playing) or ramping toward it (Opening/Buffering — e.g.
        // the DRM/CDM licensing handshake): keep pumping so frames advance and the DRM transport is driven to actually
        // play. Gated so a paused, stopped, ended, or audio-only player lets the loop idle:
        //  • audio-only sessions don't implement IVideoSurfaceSession (video-capable check below);
        //  • a resolved audio-only MF source (video-capable but no natural size) only counts while still ramping.
        // Read back after the pump so it reflects the state the session just published.
        if (binding.IsValid)
        {
            bool videoCapable = _session is IVideoSurfaceSession;
            var st = _core.State.Peek();
            bool ramping = st is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled;
            bool advancing = st == PlaybackState.Playing && !_core.NaturalSize.Peek().IsEmpty;
            bool presenting = videoCapable && _core.IsPlayRequested.Peek() && (ramping || advancing);
            binding.SetPresenting(presenting);
        }
    }

    // ── transport (forward to the routed session; keep intent coherent on the core) ──────────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.SetPlayRequested(true);
        return _session?.PlayAsync() ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _core.SetPlayRequested(false);
        return _session?.PauseAsync() ?? ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_disposed) return;
        _core.SetPlayRequested(false);
        _core.SetState(PlaybackState.Idle);
        _core.SettleTransport();
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode = SeekMode.Accurate)
        => _disposed ? ValueTask.CompletedTask : (_session?.SeekAsync(to, mode) ?? ValueTask.CompletedTask);

    /// <inheritdoc/>
    public ValueTask StepFrame(int delta)
        => _disposed ? ValueTask.CompletedTask : (_session?.SeekAsync(_core.Position.Peek() + TimeSpan.FromMilliseconds(33.0 * delta), SeekMode.Accurate) ?? ValueTask.CompletedTask);

    /// <inheritdoc/>
    public void SetRate(double rate) { if (_disposed) return; _core.Rate.Value = (float)rate; _session?.SetRate(rate); }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (_disposed) return; _core.Volume.Value = (float)Math.Clamp(volume, 0, 1); _session?.SetVolume(volume); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (_disposed) return; _core.SetMuted(muted); _session?.SetMuted(muted); }

    // ── source + queue + preroll ─────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask OpenAsync(MediaSource source, CancellationToken ct = default)
    {
        if (_disposed) return;
        var kind = MediaKindSniffer.Sniff(source);
        var backend = _router.Resolve(kind);
        if (backend is null)
        {
            _core.SetError(MediaError.NoBackend(kind));
            _core.SetState(PlaybackState.Failed);
            return;
        }

        // Backend switch (spec §12): only a KIND change recreates the inner session; same kind reuses across sources.
        if (_session is not null && kind != _currentKind)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }

        _core.SetError(null);
        _core.SetState(PlaybackState.Opening);
        var opts = new MediaOpenOptions { StartPaused = true, Buffering = _buffering, LicenseRelay = _licenseRelay };
        try
        {
            var session = await backend.OpenAsync(source, opts, ct).ConfigureAwait(false);
            _session = session;
            _currentKind = kind;
            session.ConnectSignals(_sink);
        }
        catch (OperationCanceledException)
        {
            // Open canceled (dispose mid-open) — complete quietly, never crash (spec §12).
        }
        catch (Exception ex)
        {
            _core.SetError(new MediaError(MediaErrorCategory.Source, ex.Message, null, new MediaLocus(null, source, null, null, null), MediaRecovery.Retryable));
            _core.SetState(PlaybackState.Failed);
        }
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _core.SettleTransport();
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}

/// <summary>
/// The router (spec §4.1/§4.4): resolves a <see cref="MediaKind"/> to a registered <see cref="IMediaBackend"/>, with
/// <see cref="MediaKind.Auto"/> sniffing handled by <see cref="MediaKindSniffer"/> before resolution. Empty by default —
/// backends (MF video M1, PCM audio M2) register themselves; the facade surfaces a typed error for an unresolved kind.
/// </summary>
public sealed class MediaRouter
{
    private readonly Dictionary<MediaKind, IMediaBackend> _backends = new();

    /// <summary>Register a backend for a concrete kind (PcmAudio / MfVideoOrFile).</summary>
    public void Register(MediaKind kind, IMediaBackend backend)
    {
        if (kind == MediaKind.Auto) throw new ArgumentException("Register a concrete kind, not Auto.", nameof(kind));
        _backends[kind] = backend;
    }

    /// <summary>Resolve a concrete kind to a backend, or null when none is registered.</summary>
    public IMediaBackend? Resolve(MediaKind kind)
        => _backends.TryGetValue(kind, out var b) ? b : null;

    /// <summary>True when a backend is registered for the kind.</summary>
    public bool Has(MediaKind kind) => _backends.ContainsKey(kind);
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
    private readonly MediaRouter _router = new();
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
