using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The protected-video <see cref="IMediaBackend"/> (spec §9.2) — the DRM path the Windows MF backend routes to when a
/// <see cref="MediaSource"/> carries a <see cref="DrmConfig"/>. It opens a <see cref="ProtectedMediaSession"/> backed by the
/// in-process native PlayReady CDM (<see cref="DesktopProtectedVideoPlayer"/>) and threads the app license relay
/// (<see cref="MediaOpenOptions.LicenseRelay"/>, from <c>WithDrm</c>) down to the native callback. Also
/// <see cref="IPreparableBackend"/>: a queued protected item can be spun up + first-frame-readied ahead of a mixed-queue
/// join (the two engines never co-mix — a cross-backend transition is a declicked hard cut).
/// <para>Testable: inject a fake <see cref="IProtectedVideoPlayer"/> factory to exercise routing / snapshot mapping /
/// prepare without a real CDM or native call.</para>
/// </summary>
public sealed class ProtectedMediaBackend : IMediaBackend, IPreparableBackend
{
    private readonly Func<IProtectedVideoPlayer> _playerFactory;
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _defaultRelay;
    private readonly TimeSpan _prepareTimeout;
    private readonly DashSourceDescriptor? _descriptor;

    /// <summary>Create the production backend (each open builds a real in-process native CDM player). An optional
    /// <paramref name="defaultRelay"/> is used for the prepare hook (which has no per-open options). Pass a
    /// <paramref name="descriptor"/> (from <see cref="DashManifestParser"/>) to play an ARBITRARY parsed DASH/PlayReady
    /// source; omit it to fall back to the baked Axinom test vector for a recognized URI.</summary>
    public ProtectedMediaBackend(Func<LicenseRequest, ValueTask<LicenseResponse>>? defaultRelay = null,
                                 DashSourceDescriptor? descriptor = null)
        : this(static () => new DesktopProtectedVideoPlayer(), defaultRelay, null, descriptor) { }

    /// <summary>Test/DI seam: supply the protected-player factory + optional prepare timeout + optional parsed source
    /// descriptor.</summary>
    public ProtectedMediaBackend(Func<IProtectedVideoPlayer> playerFactory,
                                 Func<LicenseRequest, ValueTask<LicenseResponse>>? defaultRelay = null,
                                 TimeSpan? prepareTimeout = null,
                                 DashSourceDescriptor? descriptor = null)
    {
        _playerFactory = playerFactory;
        _defaultRelay = defaultRelay;
        _prepareTimeout = prepareTimeout ?? TimeSpan.FromSeconds(10);
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: true)
    {
        IsSupported = static ct => ct.Video is CodecId.None or CodecId.H264 or CodecId.Hevc,
    };

    /// <inheritdoc/>
    public MediaKind Kind => MediaKind.MfVideoOrFile;

    /// <inheritdoc/>
    public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        if (source.Drm is null)
            throw new NotSupportedException("ProtectedMediaBackend requires a source carrying a DrmConfig (source.With(drm)).");

        var request = BuildRequest(source, source.Drm, opts.LicenseRelay ?? _defaultRelay, opts.StartPaused, _descriptor);
        var player = _playerFactory();
        IMediaSession session = new ProtectedMediaSession(player, request, opts);
        return ValueTask.FromResult(session);
    }

    /// <inheritdoc/>
    public async ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
    {
        if (next.Drm is null)
            throw new NotSupportedException("ProtectedMediaBackend.PrepareAsync requires a source carrying a DrmConfig.");

        var request = BuildRequest(next, next.Drm, _defaultRelay, startPaused: true, _descriptor);
        var player = _playerFactory();
        var opts = new MediaOpenOptions { StartPaused = true, LicenseRelay = _defaultRelay };
        var session = new ProtectedMediaSession(player, request, opts);

        // Spin up the CDM + first-frame-ready ahead of the join (bounded). The join consumes it as a hard cut.
        player.Start(request);
        var deadline = DateTime.UtcNow + _prepareTimeout;
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            player.Pump(default);   // refresh the native snapshot (no real binding at prepare time)
            if (player.HasSurface || player.State.Value is ProtectedVideoState.Playing or ProtectedVideoState.Paused)
                break;
            if (player.State.Value == ProtectedVideoState.Error) break;
            try { await Task.Delay(20, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }

        bool ready = player.HasSurface || player.State.Value is ProtectedVideoState.Playing or ProtectedVideoState.Paused;
        var duration = player.DurationMs.Value > 0 ? TimeSpan.FromMilliseconds(player.DurationMs.Value) : TimeSpan.Zero;
        return new ProtectedPreparedItem(session, ready, duration);
    }

    /// <summary>Map a <see cref="MediaSource"/> + <see cref="DrmConfig"/> + relay into a native open request. When a parsed
    /// <paramref name="descriptor"/> is supplied (from <see cref="DashManifestParser"/>) it carries the source verbatim —
    /// ANY DASH/PlayReady MPD, not just the test vector. Otherwise a recognized Axinom URI is expanded to its known
    /// init+segment template, and an unrecognized URI leaves the template empty (native falls back to its baked vector).</summary>
    internal static ProtectedVideoRequest BuildRequest(MediaSource source, DrmConfig drm,
        Func<LicenseRequest, ValueTask<LicenseResponse>>? relay, bool startPaused, DashSourceDescriptor? descriptor = null)
    {
        var req = new ProtectedVideoRequest
        {
            Source = source,
            Drm = drm,
            LicenseRelay = relay,
            StartPaused = startPaused,
            Mode = "protected-custom",
        };

        // Preferred generic path: a parsed manifest descriptor drives the native open ABI directly.
        if (descriptor is not null)
        {
            return req with
            {
                InitUrl = descriptor.InitUrl,
                SegmentBaseUrl = descriptor.SegmentBaseUrl,
                SegmentPrefix = descriptor.SegmentPrefix,
                SegmentSuffix = descriptor.SegmentSuffix,
                StartNumber = descriptor.StartNumber,
                SegmentCount = descriptor.SegmentCount,
                SegmentStride = descriptor.SegmentStride,
                Pssh = descriptor.Pssh,
            };
        }

        string? uri = ExtractUri(source);
        // Legacy fallback — Axinom public singlekey PlayReady vector: expand its MPD to the explicit init/segment template.
        if (uri is not null && uri.Contains("protected_dash_1080p_h264_singlekey", StringComparison.OrdinalIgnoreCase))
        {
            const string baseUrl = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/";
            req = req with
            {
                InitUrl = baseUrl + "video-H264-720-2100k_init.mp4",
                SegmentBaseUrl = baseUrl,
                SegmentPrefix = "video-H264-720-2100k_",
                SegmentSuffix = ".m4s",
                StartNumber = 1,
                SegmentCount = 6,
            };
        }
        return req;
    }

    private static string? ExtractUri(MediaSource source) => source switch
    {
        UriSource u => u.Url,
        FileSource f => f.Path,
        ClipSource c => ExtractUri(c.Inner),
        LoopSource l => ExtractUri(l.Inner),
        _ => null,
    };
}

/// <summary>A pre-rolled protected item (spec §8.4): no audio voice — it carries the spun-up
/// <see cref="ProtectedMediaSession"/> the coordinator hard-cuts to at the boundary.</summary>
public sealed class ProtectedPreparedItem : IPreparedItem
{
    private readonly ProtectedMediaSession _session;

    /// <summary>Create a prepared protected item over <paramref name="session"/>.</summary>
    public ProtectedPreparedItem(ProtectedMediaSession session, bool ready, TimeSpan duration)
    {
        _session = session;
        IsReady = ready;
        Duration = duration;
    }

    /// <inheritdoc/>
    public MediaKind Kind => MediaKind.MfVideoOrFile;
    /// <inheritdoc/>
    public bool IsReady { get; }
    /// <inheritdoc/>
    public IAudioSource? AudioVoice => null;
    /// <inheritdoc/>
    public GaplessInfo Gapless => GaplessInfo.None;
    /// <inheritdoc/>
    public ReplayGainInfo Loudness => default;
    /// <inheritdoc/>
    public long TotalFrames => -1;
    /// <inheritdoc/>
    public TimeSpan Duration { get; }
    /// <inheritdoc/>
    public object? BackendHandle => _session;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
