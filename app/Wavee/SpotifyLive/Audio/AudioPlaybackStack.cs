using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Composes the local-audio stack: key resolver, head client, and in-process audio host.</summary>
public sealed class AudioPlaybackStack : IAsyncDisposable
{
    public AudioRuntimeStatusService Status { get; }
    public PlayPlayRuntimeProvisioner Provisioner { get; }
    public AudioKeyResolver KeyResolver { get; }
    public HeadFileClient HeadClient { get; }
    public InProcessAudioHost Host { get; }
    public LiveTrackResolver TrackResolver { get; }
    public RuntimeAsset? RuntimeAsset { get; private set; }
    readonly Action<string>? _log;
    readonly IWaveeLog? _structuredLog;
#if WAVEE_PLAYPLAY_LOCAL
    InProcessPlayPlayKeyDeriver? _playPlay;
#endif

    public AudioPlaybackStack(
        ITransport transport,
        IHttpExchange http,
        Func<ApConnection?> apChannel,
        Func<SessionContext> session,
        ExtendedMetadataSource extendedMetadata,
        IAppSettings settings,
        Action<string>? log = null,
        IWaveeLog? structuredLog = null)
    {
        _log = log;
        _structuredLog = structuredLog;
        Status = new AudioRuntimeStatusService();
        Provisioner = new PlayPlayRuntimeProvisioner(settings, Status, log, structuredLog: structuredLog);
#if WAVEE_PLAYPLAY_LOCAL
        Func<IPlayPlayKeyDeriver?> deriver = () => _playPlay;
#else
        Func<IPlayPlayKeyDeriver?> deriver = () => null;
#endif
        Func<RuntimeAsset?> runtime = () => RuntimeAsset;
        var license = new PlayPlayLicenseClient(transport, log, structuredLog);
        var apKeys = new LiveAudioKeySource(apChannel);
        KeyResolver = new AudioKeyResolver(apKeys, deriver, runtime, license, Status, session, log, structuredLog);
        HeadClient = new HeadFileClient(new HttpClientExchange(), session, log);
        Host = new InProcessAudioHost(
#if WAVEE_PLAYPLAY_LOCAL
            () => _playPlay,
#else
            () => null,
#endif
            log);
        Func<string, CancellationToken, Task<ByteString?>> fetchTrackV4 = (uri, ct) => extendedMetadata.GetExtensionAsync(uri, Xm.ExtensionKind.TrackV4, ct);
        Func<string, CancellationToken, Task<ByteString?>> fetchAudioFilesV5 = (uri, ct) => extendedMetadata.GetExtensionAsync(uri, Xm.ExtensionKind.AudioFiles, ct);
        Func<string, Xm.ExtensionKind, CancellationToken, Task<ByteString?>> fetchAnyExtension =
            (uri, kind, ct) => extendedMetadata.GetExtensionAsync(uri, kind, ct);
        var formatProbe = AudioFormatProbe.FromEnvironment(transport, http, fetchAnyExtension, log);
        if (formatProbe is not null) log?.Invoke("audio format probe enabled (WAVEE_AUDIO_FORMAT_PROBE=1)");
        TrackResolver = new LiveTrackResolver(transport, KeyResolver, fetchTrackV4, fetchAudioFilesV5,
            preferLossless: false, log, formatProbe,
            // The persisted streaming-quality preference, read per resolve so a Settings change applies from the next
            // track. Clamped to the Ogg tiers — Lossless is reserved (the picker shows it disabled, "Coming soon").
            quality: () => (AudioQualityPreference)Math.Clamp(settings.Get(WaveeSettings.PlaybackQuality), 0, 2));
        Log(WaveeLogLevel.Debug, "audio.stack.created", "Audio playback stack created",
            WaveeLogField.Of("playplayLocal", PlayPlayLocalCompiled),
            WaveeLogField.Of("formatProbe", formatProbe is not null));
    }

    /// <summary>Background provision — off the startup path.</summary>
    public void StartProvisioning(CancellationToken ct)
    {
        Log(WaveeLogLevel.Debug, "playplay.provision.scheduled", "PlayPlay provisioning scheduled");
        _ = ProvisionAndBindAsync(ct);
    }

    async Task ProvisionAndBindAsync(CancellationToken ct)
    {
        try
        {
            Log(WaveeLogLevel.Debug, "playplay.provision.start", "PlayPlay provisioning started");
            var asset = await Provisioner.EnsureRuntimeAsync(ct).ConfigureAwait(false);
            if (asset is not null)
                BindPlayPlay(asset, "startup");
            else
                Log(WaveeLogLevel.Warning, "playplay.provision.no_asset", "PlayPlay provisioning found no usable runtime",
                    WaveeLogField.Of("outcome", Provisioner.GetSnapshot().Outcome.ToString()));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogException(WaveeLogLevel.Error, "playplay.provision.failed", "PlayPlay provisioning crashed", ex);
            Status.SetProvisioning(ProvisioningOutcome.RuntimeUnavailable, ex.Message);
        }
    }

    public async Task<bool> TryRefreshPlayPlayRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false)
    {
        Log(WaveeLogLevel.Info, "playplay.refresh.start", "Refreshing PlayPlay runtime",
            WaveeLogField.Of("allowUntrusted", allowUntrustedSignature));
        var asset = await Provisioner.EnsureRuntimeAsync(ct, allowUntrustedSignature).ConfigureAwait(false);
        if (asset is null)
        {
            Log(WaveeLogLevel.Warning, "playplay.refresh.no_asset", "Refresh found no usable PlayPlay runtime",
                WaveeLogField.Of("outcome", Provisioner.GetSnapshot().Outcome.ToString()));
            return false;
        }
        return BindPlayPlay(asset, "refresh");
    }

    bool BindPlayPlay(RuntimeAsset asset, string reason)
    {
        RuntimeAsset = asset;
        Log(WaveeLogLevel.Info, "playplay.bind.start", "Binding PlayPlay runtime",
            WaveeLogField.Of("reason", reason),
            WaveeLogField.Of("pack", asset.PackId),
            WaveeLogField.Of("version", asset.Config.Version),
            WaveeLogField.Of("arch", asset.Config.Arch.ToString()),
            WaveeLogField.Of("dll", asset.PackPath));
#if WAVEE_PLAYPLAY_LOCAL
        _playPlay?.Dispose();
        _playPlay = InProcessPlayPlayKeyDeriver.TryCreate(asset, Status, _log);
        Host.RebindPlayPlay(() => _playPlay);
        if (_playPlay is null)
        {
            Status.SetProvisioning(ProvisioningOutcome.RuntimeUnavailable, "PlayPlay native deriver init failed");
            Log(WaveeLogLevel.Error, "playplay.bind.failed", "PlayPlay runtime pack is present but native deriver did not bind",
                WaveeLogField.Of("pack", asset.PackId),
                WaveeLogField.Of("version", asset.Config.Version),
                WaveeLogField.Of("arch", asset.Config.Arch.ToString()));
            return false;
        }
        Status.SetProvisioning(ProvisioningOutcome.Ready);
        Log(WaveeLogLevel.Info, "playplay.bind.ready", "PlayPlay native deriver bound",
            WaveeLogField.Of("pack", asset.PackId),
            WaveeLogField.Of("version", asset.Config.Version),
            WaveeLogField.Of("arch", asset.Config.Arch.ToString()));
        return true;
#else
        Status.SetProvisioning(ProvisioningOutcome.RuntimeUnavailable, "Wavee was built without WAVEE_PLAYPLAY_LOCAL");
        Log(WaveeLogLevel.Error, "playplay.bind.not_compiled", "PlayPlay runtime pack is present but local deriver code is not compiled",
            WaveeLogField.Of("pack", asset.PackId),
            WaveeLogField.Of("version", asset.Config.Version),
            WaveeLogField.Of("arch", asset.Config.Arch.ToString()));
        return false;
#endif
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
#if WAVEE_PLAYPLAY_LOCAL
        _playPlay?.Dispose();
#endif
    }

    void Log(WaveeLogLevel level, string eventId, string message, params WaveeLogField[] fields)
    {
        _structuredLog?.Event(level, "audio", eventId, message, fields: fields);
    }

    void LogException(WaveeLogLevel level, string eventId, string message, Exception ex, params WaveeLogField[] fields)
    {
        _structuredLog?.Event(level, "audio", eventId, message, ex: ex, fields: fields);
    }

    static bool PlayPlayLocalCompiled
    {
        get
        {
#if WAVEE_PLAYPLAY_LOCAL
            return true;
#else
            return false;
#endif
        }
    }
}

/// <summary>Fast-first resolve: metadata → then head GET and key/CDN resolve IN PARALLEL. The head (no key needed) lets
/// playback start immediately; the body (key + CDN) is handed back as a Task the controller supplies to the host when it
/// lands. This is what hides the key/derive latency behind the head's ~3 s of clear audio.</summary>
public sealed class FastTrackPlayback : IFastTrackResolver, IFastTrackWarmer
{
    readonly LiveTrackResolver _resolver;
    readonly HeadFileClient _heads;
    readonly Action<string>? _log;
    readonly ConcurrentDictionary<string, byte> _warmInFlight = new(StringComparer.Ordinal);
    long _warmQuietUntilTicks;

    static readonly TimeSpan AutoWarmDelay = TimeSpan.FromSeconds(6);
    static readonly TimeSpan ForegroundWarmQuiet = TimeSpan.FromSeconds(10);

    public FastTrackPlayback(LiveTrackResolver resolver, HeadFileClient heads, Action<string>? log = null)
    {
        _resolver = resolver;
        _heads = heads;
        _log = log;
    }

    public async Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
    {
        MarkWarmQuietWindow();
        var sw = Stopwatch.StartNew();
        _log?.Invoke($"fast-resolve {track.Uri}: meta start");
        var meta = await _resolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);
        _log?.Invoke($"fast-resolve {track.Uri}: meta ok file={meta.FileIdHex} fmt={meta.Fmt} elapsed={sw.ElapsedMilliseconds}ms");

        var headSw = Stopwatch.StartNew();
        var headTask = _heads.GetAsync(meta.FileIdHex, ct);
        var rawBodyTask = _resolver.ResolveBodyAsync(meta, ct);

        var head = await headTask.ConfigureAwait(false);
        _log?.Invoke($"fast-resolve {track.Uri}: head ok file={meta.FileIdHex} bytes={head.Data.Length} elapsed={headSw.ElapsedMilliseconds}ms total={sw.ElapsedMilliseconds}ms");
        var start = new AudioFastStart(track.Uri, meta.FileIdHex, meta.Fmt, meta.DurMs, head.NormalizationGainDb, head.Data);
        var bodyTask = FinishBodyAsync(rawBodyTask, head.Data.Length, head.NormalizationGainDb, _log, track.Uri, meta.FileIdHex, sw.ElapsedMilliseconds);
        _log?.Invoke($"fast-resolve {track.Uri}: plan ready file={meta.FileIdHex} head={head.Data.Length}B bodyCompleted={rawBodyTask.IsCompleted} total={sw.ElapsedMilliseconds}ms");
        return new FastStartPlan(start, bodyTask);
    }

    public void Warm(Track track, string reason = "")
    {
        if (!_warmInFlight.TryAdd(track.Uri, 0))
        {
            _log?.Invoke($"fast-warm {track.Uri}: skipped duplicate reason={reason}");
            return;
        }

        var delay = reason is "context-set" or "after-start" ? AutoWarmDelay : TimeSpan.Zero;
        _ = WarmAsync(track, reason, delay);
    }

    async Task WarmAsync(Track track, string reason, TimeSpan delay)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (delay > TimeSpan.Zero)
            {
                _log?.Invoke($"fast-warm {track.Uri}: scheduled reason={reason} delay={delay.TotalMilliseconds:0}ms");
                await Task.Delay(delay).ConfigureAwait(false);
            }
            while (WarmQuietRemaining() is { } quiet && quiet > TimeSpan.Zero)
            {
                _log?.Invoke($"fast-warm {track.Uri}: deferred reason={reason} foregroundQuiet={quiet.TotalMilliseconds:0}ms");
                await Task.Delay(quiet).ConfigureAwait(false);
            }
            _log?.Invoke($"fast-warm {track.Uri}: start reason={reason}");
            var meta = await _resolver.ResolveMetaAsync(track, CancellationToken.None).ConfigureAwait(false);
            var head = await _heads.GetAsync(meta.FileIdHex, CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke($"fast-warm {track.Uri}: ok file={meta.FileIdHex} head={head.Data.Length}B elapsed={sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"fast-warm {track.Uri}: failed elapsed={sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _warmInFlight.TryRemove(track.Uri, out _);
        }
    }

    void MarkWarmQuietWindow()
    {
        var until = Stopwatch.GetTimestamp() + (long)(ForegroundWarmQuiet.TotalSeconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref _warmQuietUntilTicks, until);
    }

    TimeSpan WarmQuietRemaining()
    {
        var until = Interlocked.Read(ref _warmQuietUntilTicks);
        if (until <= 0) return TimeSpan.Zero;
        var remainingTicks = until - Stopwatch.GetTimestamp();
        return remainingTicks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    static async Task<AudioStreamHandle> FinishBodyAsync(Task<AudioStreamHandle> raw, int headBoundary, float gainDb,
        Action<string>? log, string trackUri, string fileIdHex, long startedAtMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var h = await raw.ConfigureAwait(false);
            log?.Invoke($"fast-resolve {trackUri}: body ok file={fileIdHex} elapsed={sw.ElapsedMilliseconds}ms sinceStart={startedAtMs + sw.ElapsedMilliseconds}ms cdn={h.CdnUrls?.Length ?? 0}");
            return h with { HeadBoundary = headBoundary, NormalizationGainDb = gainDb };
        }
        catch (Exception ex)
        {
            log?.Invoke($"fast-resolve {trackUri}: body failed file={fileIdHex} elapsed={sw.ElapsedMilliseconds}ms sinceStart={startedAtMs + sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
