using System;
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

/// <summary>Composes the local-audio stack: provisioner, key resolver, head client, x64 host proxy.</summary>
public sealed class AudioPlaybackStack : IAsyncDisposable
{
    public AudioRuntimeStatusService Status { get; }
    public AudioRuntimeProvisioner Provisioner { get; }
    public AudioKeyResolver KeyResolver { get; }
    public HeadFileClient HeadClient { get; }
    public RemoteAudioHost Host { get; }
    public LiveTrackResolver TrackResolver { get; }

    public AudioPlaybackStack(
        ITransport transport,
        IHttpExchange http,
        Func<ApConnection?> apChannel,
        Func<SessionContext> session,
        ExtendedMetadataSource extendedMetadata,
        Action<string>? log = null)
    {
        Status = new AudioRuntimeStatusService();
        Provisioner = new AudioRuntimeProvisioner(http, Status, log);
        var proc = new AudioProcessManager(log);
        var deriver = new IpcPlayPlayKeyDeriver(proc, Status, log);
        var license = new PlayPlayLicenseClient(transport, log);
        var apKeys = new LiveAudioKeySource(apChannel);
        KeyResolver = new AudioKeyResolver(apKeys, deriver, () => Provisioner.Current, license, Status, session, log);
        HeadClient = new HeadFileClient(http, session, log);
        Host = new RemoteAudioHost(proc, log);
        // File IDs come from extended-metadata (TRACK_V4 = Ogg/AAC; AUDIO_FILES = FLAC), sharing the app's cache/session.
        Func<string, CancellationToken, Task<ByteString?>> fetchTrackV4 = (uri, ct) => extendedMetadata.GetExtensionAsync(uri, Xm.ExtensionKind.TrackV4, ct);
        Func<string, CancellationToken, Task<ByteString?>> fetchAudioFilesV5 = (uri, ct) => extendedMetadata.GetExtensionAsync(uri, Xm.ExtensionKind.AudioFiles, ct);
        TrackResolver = new LiveTrackResolver(transport, KeyResolver, fetchTrackV4, fetchAudioFilesV5, preferLossless: false, log);
    }

    /// <summary>Background provision — off the startup path.</summary>
    public void StartProvisioning(CancellationToken ct) =>
        _ = Task.Run(async () => { try { await Provisioner.ProvisionAsync(ct).ConfigureAwait(false); } catch { } }, ct);

    public async ValueTask DisposeAsync() => await Host.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Fast-first resolve: metadata → then head GET and key/CDN resolve IN PARALLEL. The head (no key needed) lets
/// playback start immediately; the body (key + CDN) is handed back as a Task the controller supplies to the host when it
/// lands. This is what hides the key/derive latency behind the head's ~3 s of clear audio.</summary>
public sealed class FastTrackPlayback : IFastTrackResolver
{
    readonly LiveTrackResolver _resolver;
    readonly HeadFileClient _heads;
    readonly Action<string>? _log;

    public FastTrackPlayback(LiveTrackResolver resolver, HeadFileClient heads, Action<string>? log = null)
    {
        _resolver = resolver;
        _heads = heads;
        _log = log;
    }

    public async Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
    {
        var meta = await _resolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);   // fast: metadata + file select

        // Kick head + body concurrently. The head needs no key → usually arrives first and starts playback; the body
        // (storage-resolve + key/derive) resolves behind it, within the head's playback window.
        var headTask = _heads.GetAsync(meta.FileIdHex, ct);
        var rawBodyTask = _resolver.ResolveBodyAsync(meta, ct);

        var head = await headTask.ConfigureAwait(false);
        var start = new AudioFastStart(track.Uri, meta.FileIdHex, meta.Fmt, meta.DurMs, head.NormalizationGainDb, head.Data);
        var bodyTask = FinishBodyAsync(rawBodyTask, head.Data.Length, head.NormalizationGainDb);
        return new FastStartPlan(start, bodyTask);
    }

    // Stamp the head boundary (where the clear head ends and the encrypted body begins) + gain onto the resolved body.
    static async Task<AudioStreamHandle> FinishBodyAsync(Task<AudioStreamHandle> raw, int headBoundary, float gainDb)
    {
        var h = await raw.ConfigureAwait(false);
        return h with { HeadBoundary = headBoundary, NormalizationGainDb = gainDb };
    }
}
