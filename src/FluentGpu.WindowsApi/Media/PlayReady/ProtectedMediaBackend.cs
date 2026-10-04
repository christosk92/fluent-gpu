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
/// attach. An unconsumed prepared session is disposed <see cref="PreparedExpiryMs"/> after its fetch LANDED (a
/// prepare that is still downloading is not aged by its download), and every open's outcome is logged as
/// <c>[video] prepared.hit|miss|expired key= ageMs=</c>, so a lost hand-off is never silent. A host asks
/// <see cref="TryPeekPrepared"/> whether the open would be handed one, under the same expiry rule.</para>
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
    /// <summary>How long a prepared-but-unopened session is kept, measured from the moment its fetch landed (from its
    /// creation while it has not landed), before it is disposed (its store and license row stay accounted for no longer
    /// than the runtime's own warm-idle window).</summary>
    public const int PreparedExpiryMs = ProtectedVideoRuntime.WarmIdleDisposeMs;

    private readonly Func<ProtectedVideoRequest, IProtectedVideoPlayer> _playerFactory;
    private readonly Action<ProtectedVideoRequest>? _ensureLicense;
    private readonly Func<LicenseRequest, ValueTask<LicenseResponse>>? _defaultRelay;
    private readonly DashSourceDescriptor? _descriptor;
    private readonly object _gate = new();
    private readonly Dictionary<string, PreparedEntry> _prepared = new(StringComparer.Ordinal);

    /// <summary>The millisecond clock the expiry rule reads (a test advances it instead of waiting out
    /// <see cref="PreparedExpiryMs"/>).</summary>
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;

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
    public ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
        => PrepareCoreAsync(next, TimeSpan.Zero, ct);

    /// <summary>
    /// Prepare <paramref name="source"/> to open AT <paramref name="startPosition"/>: the license goes in flight, and the
    /// init segments plus <see cref="ProtectedVideoSession.DefaultPrefetchSegments"/> segments AROUND that position are
    /// fetched for both streams, with no engine call. This is the host's entry for the CURRENT track (a badge lit
    /// mid-song: the switch will carry the song's position), where the queue's <see cref="PrepareAsync"/> prepares the
    /// NEXT one from its start. The matching <see cref="OpenAsync"/> (same init URL) takes the prepared session; an open
    /// at a slightly later position moves its start before attaching, inside the fetched window or one segment on.
    /// </summary>
    public ValueTask<IPreparedItem> PrepareAtAsync(MediaSource source, TimeSpan startPosition, CancellationToken ct = default)
        => PrepareCoreAsync(source, startPosition > TimeSpan.Zero ? startPosition : TimeSpan.Zero, ct);

    private async ValueTask<IPreparedItem> PrepareCoreAsync(MediaSource next, TimeSpan startPosition, CancellationToken ct)
    {
        if (next.Drm is null)
            throw new NotSupportedException("ProtectedMediaBackend.PrepareAsync requires a source carrying a DrmConfig.");

        ProtectedVideoRequest request = BuildRequest(next, next.Drm, _defaultRelay, startPaused: true, _descriptor)
            with { StartPosition = startPosition };
        _ensureLicense?.Invoke(request);                                   // the license is in flight from THIS line
        IProtectedVideoPlayer player = _playerFactory(request);
        var entry = new PreparedEntry(this, player, request);
        AddPrepared(entry);

        try
        {
            await player.PrefetchAsync(ProtectedVideoSession.DefaultPrefetchSegments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* a canceled prepare still leaves whatever landed usable */ }
        entry.MarkLanded(Clock());                                         // the expiry window starts HERE, not at creation

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
            long now = Clock();
            foreach (KeyValuePair<string, PreparedEntry> kv in _prepared)
                if (kv.Value.IsExpired(now)) (expired ??= new()).Add(kv.Value);
            if (expired is not null)
                for (int i = 0; i < expired.Count; i++) _prepared.Remove(expired[i].Request.InitUrl!);
            if (_prepared.Remove(key, out PreparedEntry? superseded)) (expired ??= new()).Add(superseded);
            _prepared[key] = entry;
        }
        if (expired is not null)
            for (int i = 0; i < expired.Count; i++) expired[i].Release();
    }

    /// <summary>Hand a prepared player for <paramref name="request"/>'s init URL to the open, exactly once. Logs the
    /// outcome: <c>hit</c> (taken), <c>expired</c> (found, past <see cref="PreparedExpiryMs"/>, disposed) or <c>miss</c>
    /// (none registered, or its item was disposed first).</summary>
    private IProtectedVideoPlayer? TakePrepared(ProtectedVideoRequest request)
    {
        string? key = request.InitUrl;
        if (key is null) return null;
        PreparedEntry? entry = null;
        lock (_gate)
        {
            if (_prepared.Remove(key, out PreparedEntry? taken)) entry = taken;
        }
        if (entry is null) { LogPrepared("miss", key, -1); return null; }
        long now = Clock();
        long age = entry.AgeMs(now);
        if (entry.IsExpired(now)) { entry.Release(); LogPrepared("expired", key, age); return null; }
        if (!entry.Claim()) { LogPrepared("miss", key, age); return null; }
        LogPrepared("hit", key, age);
        return entry.Player;
    }

    /// <summary>
    /// Would an open of the source whose init URL is <paramref name="initUrl"/> be handed a prepared session right now?
    /// True while one is registered (in flight or landed), not yet claimed and within <see cref="PreparedExpiryMs"/> —
    /// the exact rule <see cref="OpenAsync"/> applies, so a host reports "warm" only for a switch that will be. An
    /// expired entry found here is disposed on the spot (it would be at the open anyway, and nothing else frees it
    /// until the next prepare). Does not claim or refresh anything.
    /// </summary>
    public bool TryPeekPrepared(string? initUrl)
    {
        if (initUrl is null) return false;
        PreparedEntry? expired = null;
        bool live = false;
        long age = 0;
        lock (_gate)
        {
            if (!_prepared.TryGetValue(initUrl, out PreparedEntry? entry)) return false;
            long now = Clock();
            age = entry.AgeMs(now);
            if (entry.IsExpired(now)) { _prepared.Remove(initUrl); expired = entry; }
            else live = !entry.IsClaimed;
        }
        if (expired is null) return live;
        expired.Release();
        LogPrepared("expired", initUrl, age);
        return false;
    }

    private static void LogPrepared(string what, string initUrl, long ageMs)
        => ProtectedVideoRuntime.WriteVideoLine($"prepared.{what} key={(initUrl.Length <= 24 ? initUrl : initUrl[^24..])} ageMs={ageMs}");

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
            CreatedMs = owner.Clock();
        }

        internal IProtectedVideoPlayer Player { get; }
        internal ProtectedVideoRequest Request { get; }
        internal long CreatedMs { get; }

        // The clock reading at which the prefetch finished (or was canceled); -1 while it is still in flight.
        private long _landedMs = -1;

        internal bool IsClaimed => Volatile.Read(ref _claimed) != 0;

        internal void MarkLanded(long nowMs) => Volatile.Write(ref _landedMs, nowMs);

        /// <summary>Milliseconds since the entry's clock started: its landing, or its creation while still in flight.</summary>
        internal long AgeMs(long nowMs)
        {
            long landed = Volatile.Read(ref _landedMs);
            return nowMs - (landed >= 0 ? landed : CreatedMs);
        }

        internal bool IsExpired(long nowMs) => AgeMs(nowMs) > PreparedExpiryMs;

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
