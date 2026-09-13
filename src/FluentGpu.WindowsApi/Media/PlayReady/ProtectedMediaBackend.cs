using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The protected-video <see cref="IMediaBackend"/> (spec §9.2) — the DRM path the Windows MF backend routes to when a
/// <see cref="MediaSource"/> carries a <see cref="DrmConfig"/>. Every open is a <see cref="ProtectedVideoSession"/> on the
/// process <see cref="ProtectedVideoRuntime"/> (one warm engine, one CDM, a KID-keyed license cache), wrapped in a
/// <see cref="ProtectedMediaSession"/>.
/// <para><b>Warm switching.</b> As an <see cref="IPreparableBackend"/>, <see cref="PrepareAsync"/> does what the seam always
/// promised: the license goes in flight from its first line, and the session fetches the init segments plus
/// <see cref="ProtectedVideoSession.DefaultPrefetchSegments"/> segments at the start position for both streams — with
/// no engine call. <see cref="OpenAsync"/> then finds that prepared session by its init URL and the switch is one
/// attach. An unconsumed prepared session is disposed after <see cref="PreparedExpiryMs"/>.</para>
/// <para><b>The start position.</b> <see cref="MediaOpenOptions.StartPosition"/> is carried into the native open
/// descriptor: the first segment fetched is the one containing it and the first presented frame is at it. A song→video
/// switch at 1:23 never shows 0:00 first.</para>
/// <para><b>Per-source descriptor.</b> The parsed manifest travels ON THE SOURCE via
/// <see cref="DrmConfig.SourceDescriptor"/> (a <see cref="DashSourceDescriptor"/>), so ONE long-lived backend plays any
/// source; the ctor-baked descriptor is only a fallback for a backend pinned to one fixed track.</para>
/// <para>Testable: inject a player factory (and optionally the license hook) to exercise routing, request mapping and
/// prepare/open hand-off without a CDM or a native call.</para>
/// </summary>
public sealed class ProtectedMediaBackend : IMediaBackend, IPreparableBackend
{
    /// <summary>How long a prepared-but-unopened session is kept before it is disposed (its store and license row stay
    /// accounted for no longer than the runtime's own warm-idle window).</summary>
    public const int PreparedExpiryMs = ProtectedVideoRuntime.WarmIdleDisposeMs;

    private readonly Func<ProtectedVideoRequest, IProtectedVideoPlayer> _playerFactory;
    private readonly Action<ProtectedVideoRequest>? _ensureLicense;
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _defaultRelay;
    private readonly DashSourceDescriptor? _descriptor;
    private readonly object _gate = new();
    private readonly Dictionary<string, PreparedEntry> _prepared = new(StringComparer.Ordinal);

    /// <summary>Create the production backend: sessions on <see cref="ProtectedVideoRuntime.Shared"/>, licenses started in
    /// its cache. <paramref name="defaultRelay"/> serves the prepare hook (which has no per-open options);
    /// <paramref name="descriptor"/> is only a FALLBACK for a source that carries none of its own.</summary>
    public ProtectedMediaBackend(Func<LicenseRequest, ValueTask<LicenseResponse>>? defaultRelay = null,
                                 DashSourceDescriptor? descriptor = null)
        : this(static req => ProtectedVideoSession.Create(ProtectedVideoRuntime.Shared, req), defaultRelay, descriptor,
               static req => StartLicense(ProtectedVideoRuntime.Shared, req))
    {
    }

    /// <summary>Test/DI seam: supply the per-request player factory, the optional default relay and fallback descriptor,
    /// and the optional license hook (called with every request BEFORE its player is created — production starts the
    /// runtime's license acquisition there).</summary>
    public ProtectedMediaBackend(Func<ProtectedVideoRequest, IProtectedVideoPlayer> playerFactory,
                                 Func<LicenseRequest, ValueTask<LicenseResponse>>? defaultRelay = null,
                                 DashSourceDescriptor? descriptor = null,
                                 Action<ProtectedVideoRequest>? ensureLicense = null)
    {
        _playerFactory = playerFactory;
        _defaultRelay = defaultRelay;
        _descriptor = descriptor;
        _ensureLicense = ensureLicense;
    }

    /// <summary>Preload the native component at startup idle, so the first protected open does not pay the DLL's
    /// load + MF/PlayReady import resolution on the caller's thread. Idempotent and non-blocking.</summary>
    public static void WarmupNative() => ProtectedVideoRuntime.Warmup();

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: true)
    {
        IsSupported = static ct => ct.Video is CodecId.None or CodecId.H264 or CodecId.Hevc,
    };

    /// <inheritdoc/>
    public MediaKind Kind => MediaKind.MfVideoOrFile;

    /// <summary>
    /// Start the license for <paramref name="request"/>'s KID on <paramref name="runtime"/>, if the request names one
    /// (its declared KID or the one its PSSH carries) and carries the PSSH to generate the challenge from. Returns at
    /// once. This is the manifest-time half of the switch — call it as soon as a manifest is known.
    /// </summary>
    public static LicenseCacheState StartLicense(ProtectedVideoRuntime runtime, ProtectedVideoRequest request)
    {
        string? kid = ProtectedVideoSession.KeyIdFor(request);
        if (kid is null || request.Pssh.IsEmpty) return LicenseCacheState.None;
        return runtime.EnsureLicense(request.Pssh.Span, kid, request.LicenseRelay, request.Drm?.System ?? DrmSystem.PlayReady);
    }

    /// <inheritdoc/>
    public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        if (source.Drm is null)
            throw new NotSupportedException("ProtectedMediaBackend requires a source carrying a DrmConfig (source.With(drm)).");

        ProtectedVideoRequest request = BuildRequest(source, source.Drm, opts.LicenseRelay ?? _defaultRelay, opts.StartPaused, _descriptor)
            with { StartPosition = opts.StartPosition > TimeSpan.Zero ? opts.StartPosition : TimeSpan.Zero };

        IProtectedVideoPlayer? player = TakePrepared(request);
        if (player is null)
        {
            // Cold: start the license (it may still be pending at attach — attaching anyway is faster than waiting) and
            // create the session; its attach fetches the init + the segment at the start position, both streams at once.
            _ensureLicense?.Invoke(request);
            player = _playerFactory(request);
        }
        IMediaSession session = new ProtectedMediaSession(player, request, opts);
        return ValueTask.FromResult(session);
    }

    /// <inheritdoc/>
    public async ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
    {
        if (next.Drm is null)
            throw new NotSupportedException("ProtectedMediaBackend.PrepareAsync requires a source carrying a DrmConfig.");

        ProtectedVideoRequest request = BuildRequest(next, next.Drm, _defaultRelay, startPaused: true, _descriptor);
        _ensureLicense?.Invoke(request);                                   // the license is in flight from THIS line
        IProtectedVideoPlayer player = _playerFactory(request);
        var entry = new PreparedEntry(this, player, request);
        AddPrepared(entry);

        try
        {
            await player.PrefetchAsync(ProtectedVideoSession.DefaultPrefetchSegments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* a canceled prepare still leaves whatever landed usable */ }

        TimeSpan duration = request.DurationMs > 0 ? TimeSpan.FromMilliseconds(request.DurationMs) : TimeSpan.Zero;
        bool ready = player.ForwardBufferedMs > 0 && player.State.Peek() != ProtectedVideoState.Error;
        return new ProtectedPreparedItem(entry, ready, duration);
    }

    private void AddPrepared(PreparedEntry entry)
    {
        string? key = entry.Request.InitUrl;
        if (key is null) return;
        List<PreparedEntry>? expired = null;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            foreach (KeyValuePair<string, PreparedEntry> kv in _prepared)
                if (now - kv.Value.CreatedMs > PreparedExpiryMs) (expired ??= new()).Add(kv.Value);
            if (expired is not null)
                for (int i = 0; i < expired.Count; i++) _prepared.Remove(expired[i].Request.InitUrl!);
            if (_prepared.Remove(key, out PreparedEntry? superseded)) (expired ??= new()).Add(superseded);
            _prepared[key] = entry;
        }
        if (expired is not null)
            for (int i = 0; i < expired.Count; i++) expired[i].Release();
    }

    /// <summary>Hand a prepared player for <paramref name="request"/>'s init URL to the open, exactly once.</summary>
    private IProtectedVideoPlayer? TakePrepared(ProtectedVideoRequest request)
    {
        if (request.InitUrl is null) return null;
        PreparedEntry? entry;
        lock (_gate)
        {
            if (!_prepared.Remove(request.InitUrl, out entry)) return null;
        }
        if (Environment.TickCount64 - entry.CreatedMs > PreparedExpiryMs) { entry.Release(); return null; }
        return entry.Claim() ? entry.Player : null;
    }

    private void ForgetPrepared(PreparedEntry entry)
    {
        string? key = entry.Request.InitUrl;
        if (key is null) return;
        lock (_gate)
        {
            if (_prepared.TryGetValue(key, out PreparedEntry? current) && ReferenceEquals(current, entry))
                _prepared.Remove(key);
        }
    }

    /// <summary>
    /// Map a <see cref="MediaSource"/> + <see cref="DrmConfig"/> + relay into a protected open request. The per-source
    /// descriptor on <paramref name="drm"/> wins; <paramref name="fallbackDescriptor"/> is used only when the source carries
    /// none. With neither, a recognized Axinom test-vector URI is expanded to its known init + segment template (the KID
    /// and PSSH are then read from its init segment); any other URI yields a request with no template, which the session
    /// reports as a typed failure.
    /// </summary>
    internal static ProtectedVideoRequest BuildRequest(MediaSource source, DrmConfig drm,
        Func<LicenseRequest, ValueTask<LicenseResponse>>? relay, bool startPaused, DashSourceDescriptor? fallbackDescriptor = null)
    {
        var req = new ProtectedVideoRequest
        {
            Source = source,
            Drm = drm,
            LicenseRelay = relay,
            StartPaused = startPaused,
        };

        DashSourceDescriptor? descriptor = drm.SourceDescriptor as DashSourceDescriptor ?? fallbackDescriptor;
        if (descriptor is not null)
        {
            return req with
            {
                Catalog = descriptor.Catalog,
                InitUrl = descriptor.InitUrl,
                SegmentBaseUrl = descriptor.SegmentBaseUrl,
                SegmentPrefix = descriptor.SegmentPrefix,
                SegmentSuffix = descriptor.SegmentSuffix,
                StartNumber = descriptor.StartNumber,
                SegmentCount = descriptor.SegmentCount,
                SegmentStride = descriptor.SegmentStride,
                SegmentLengthMs = descriptor.SegmentLengthMs,
                DurationMs = descriptor.DurationMs,
                Pssh = descriptor.Pssh,
                DefaultKid = descriptor.DefaultKid,
                AudioInitUrl = descriptor.AudioInitUrl,
                AudioSegmentBaseUrl = descriptor.AudioSegmentBaseUrl,
                AudioSegmentPrefix = descriptor.AudioSegmentPrefix,
                AudioSegmentSuffix = descriptor.AudioSegmentSuffix,
                AudioCodecs = descriptor.AudioCodecs,
            };
        }

        string? uri = ExtractUri(source);
        // The Axinom public single-key PlayReady test vector (the engine's on-box protected gate): expand its MPD to the
        // explicit init/segment template.
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

    /// <summary>One prepared player waiting for its open. Claimed exactly once — by the open (which then owns it) or by
    /// the prepared item's disposal / expiry (which disposes it).</summary>
    internal sealed class PreparedEntry
    {
        private readonly ProtectedMediaBackend _owner;
        private int _claimed;

        internal PreparedEntry(ProtectedMediaBackend owner, IProtectedVideoPlayer player, ProtectedVideoRequest request)
        {
            _owner = owner;
            Player = player;
            Request = request;
            CreatedMs = Environment.TickCount64;
        }

        internal IProtectedVideoPlayer Player { get; }
        internal ProtectedVideoRequest Request { get; }
        internal long CreatedMs { get; }

        internal bool Claim() => Interlocked.Exchange(ref _claimed, 1) == 0;

        /// <summary>Dispose the player unless the open already took it.</summary>
        internal void Release()
        {
            if (!Claim()) return;
            _owner.ForgetPrepared(this);
            try { Player.Dispose(); } catch { }
        }
    }
}

/// <summary>A pre-rolled protected item (spec §8.4): no audio voice — it carries the prepared
/// <see cref="IProtectedVideoPlayer"/> the next open attaches. Disposing an item whose player was never opened disposes the
/// player; disposing one whose player the open took is a no-op.</summary>
public sealed class ProtectedPreparedItem : IPreparedItem
{
    private readonly ProtectedMediaBackend.PreparedEntry _entry;

    internal ProtectedPreparedItem(ProtectedMediaBackend.PreparedEntry entry, bool ready, TimeSpan duration)
    {
        _entry = entry;
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
    /// <summary>The prepared <see cref="IProtectedVideoPlayer"/>.</summary>
    public object? BackendHandle => _entry.Player;
    /// <inheritdoc/>
    public int MixRate => 0;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _entry.Release();
        return ValueTask.CompletedTask;
    }
}
