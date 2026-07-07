using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Gabo;

namespace Wavee.SpotifyLive;

// ── Stage 0 + H — the live Connect+playback composition (the session glue) ────────────────────────────────────────────
// Wires the whole control plane + bidirectional projection + controller + silent host + resolver onto a single dealer
// transport, and exposes the Wavee.Core seams (IPlaybackPlayer / IPlaybackState / IConnectDevices) the UI binds to. This is
// the "LiveSession owner": it captures the connection_id + announces the device, ingests cluster state, routes inbound
// commands to the controller, forwards outbound commands when another device is active, drives a SILENT host for local
// playback (real audio is the deferred host behind the same IAudioHost seam), and resolves tracks (CDN + key).
//
// ONE AP connection: the persistent AP channel for audio keys is the LOGIN socket, adopted by SpotifyLiveSpclient and
// passed in here — there is NO second handshake. If it's null (couldn't be retained), the resolver falls back gracefully.
public sealed class LiveConnect : IDisposable
{
    public NowPlayingProjection Projection { get; }
    public LiveConnectDevices Devices { get; }
    public PlaybackController Controller { get; }
    public AudioPlaybackStack? Audio => _audio;
    readonly ConnectService _connect;
    readonly DeviceStatePublisher _publisher;
    readonly ClusterIngest _ingest;
    readonly ConnectCommandRouter _commands;
    readonly IAudioHost _host;
    readonly SpotifyServerClock _clock;   // server-clock skew estimator → corrects remote-position aging
    readonly ApConnection? _apChannel;   // owned: the adopted login socket
    readonly AudioPlaybackStack? _audio; // optional local-audio stack (null = silent/stub resolver)
    readonly RawCoreStreamProjection? _gabo;
    readonly ResumePointProjection? _resume;
    readonly GaboBatcher? _gaboBatcher;

    public LiveConnect(ITransport transport, string deviceId, ApConnection? apChannel,
        IContextResolver? contexts = null, Action<string>? log = null,
        AudioPlaybackStack? audio = null, double initialVolume01 = 0.7,
        Func<CancellationToken, Task<string>>? refreshTokens = null, IAppSettings? settings = null)
    {
        _apChannel = apChannel;
        _audio = audio;

        // Server-clock estimator: probes GET /melody/v1/time over the authenticated spclient pipeline; its corrected
        // "server now" feeds the projection's remote-position aging (the offset-dependent transit term).
        _clock = new SpotifyServerClock(ct => FetchServerTimeMs(transport, ct), log);
        Projection = new NowPlayingProjection(deviceId, serverNowUnixMs: _clock.ServerNowUnixMs, initialVolume01: initialVolume01);
        Devices = new LiveConnectDevices();
        _ingest = new ClusterIngest(transport, Projection, Devices, deviceId, log, _clock.ObservePassive);

        var builder = new ConnectStateBuilder(deviceId, "Wavee", isPrivateSession: () => Projection.IsPrivateSession);
        _connect = new ConnectService(transport);   // connection-id capture only
        // The SINGLE PutState writer: NewConnection announce on the connection-id + our local player_state on playback
        // changes (so other devices/controllers see us as the active player). Re-injects the response cluster.
        _publisher = new DeviceStatePublisher(transport, deviceId, Projection, _connect.ConnectionId, () => _connect.CurrentConnectionId,
            (reason, snap, mid, isActive) => builder.BuildPutState(reason, snap, mid, isActive),
            onCluster: _ingest.OnAnnounceResponse, log: log);

        _host = audio is not null ? audio.Host : new SilentAudioHost();
        var resolver = audio?.TrackResolver ?? (ITrackResolver)new StubTrackResolver();
        // Instant-start: when the local-audio stack is present, resolve head+key in parallel and start on the clear head.
        var fast = audio is not null
            ? new FastTrackPlayback(audio.TrackResolver, audio.HeadClient, log, audio.TrackResolver.InvalidateCdn)
            : null;
        var outbound = new LiveOutboundControl(transport, deviceId, () => _connect.CurrentConnectionId);
        var gaboCtx = GaboContextFactory.Create();
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var gaboSeq = settings.Get(WaveeSettings.GaboGlobalSequence);
        _gaboBatcher = new GaboBatcher(transport, gaboCtx, initialSequenceNumber: gaboSeq, refreshTokens: refreshTokens,
            persistSequence: seq => settings.Set(WaveeSettings.GaboGlobalSequence, seq), log: log);
        _gabo = new RawCoreStreamProjection(_gaboBatcher, () => Projection.ContextUri, () => true, log);
        var herodotus = new HerodotusClient(transport, log);
        _resume = new ResumePointProjection(herodotus, () => Projection.IsPrivateSession, log);
        Controller = new PlaybackController(_host, resolver, Projection,
            contexts ?? EmptyContextResolver.Instance,
            deviceId, outbound, new IPlaybackProjection[] { _gabo, _resume, _publisher }, log,
            SpotifyClientIdentity.XpuiSnapshotVersion,   // play_origin.feature_version
            fast: fast);
        Controller.EpisodeResumeMicros = (uri, ct) => herodotus.TryGetEpisodeResumeMicrosAsync(uri, ct);
        if (audio?.TrackResolver is LiveTrackResolver ltr)
        {
            Controller.MetaResolver = async (track, ct) =>
            {
                var m = await ltr.ResolveMetaAsync(track, ct).ConfigureAwait(false);
                int kbps = m.Fmt switch
                {
                    AudioFormat.OggVorbis96 => 96,
                    AudioFormat.OggVorbis160 => 160,
                    AudioFormat.OggVorbis320 => 320,
                    AudioFormat.Flac => 1411,
                    AudioFormat.Mp3 => 160,
                    _ => 160,
                };
                string fmtLabel = m.Fmt == AudioFormat.Mp3 ? "MP3" : $"Vorbis {kbps} kbps";
                return new PlaybackTrackMeta(m.FileGid, m.FileId, kbps, fmtLabel, m.DurMs);
            };
        }

        _commands = new ConnectCommandRouter(transport, cmd => Controller.HandleRemoteCommand(cmd), log);
        Devices.TransferHandler = (id, c) => Controller.TransferToAsync(id, c);
        _clock.Start();
    }

    // Fetch the server's wall clock (Unix ms) over the authenticated spclient pipeline. GET /melody/v1/time → {"timestamp": ms}.
    // Tolerates a seconds-resolution payload (scaled to ms) so a unit change on the endpoint can't silently corrupt the offset.
    static async Task<long> FetchServerTimeMs(ITransport transport, CancellationToken ct)
    {
        var resp = await transport.Request(Channel.Spclient, "/melody/v1/time", default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return 0;
        using var doc = System.Text.Json.JsonDocument.Parse(resp.Body);
        if (!doc.RootElement.TryGetProperty("timestamp", out var t) || !t.TryGetInt64(out var ms) || ms <= 0) return 0;
        return ms < 100_000_000_000L ? ms * 1000 : ms;   // < ~1973 in ms ⇒ the payload was seconds; scale up
    }

    public void Dispose()
    {
        try { Controller.DeactivateIfActiveOwner(); } catch { }   // best-effort clean is_active=false hand-off on logout
        _commands.Dispose();
        _publisher.Dispose();
        _resume?.Dispose();
        if (_gabo is not null) _ = _gabo.DisposeAsync().AsTask();
        if (_gaboBatcher is not null) _ = _gaboBatcher.DisposeAsync().AsTask();
        _connect.Dispose();
        _ingest.Dispose();
        Controller.Dispose();
        _clock.Dispose();
        _apChannel?.Dispose();
        Projection.Dispose();
        try { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _audio?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }
}
