using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

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

    readonly ConnectService _connect;
    readonly ClusterIngest _ingest;
    readonly ConnectCommandRouter _commands;
    readonly SilentAudioHost _host;
    readonly ApConnection? _apChannel;   // owned: the adopted login socket

    public LiveConnect(ITransport transport, string deviceId, ApConnection? apChannel,
        Func<string, CancellationToken, Task<IReadOnlyList<Track>>>? resolveContext = null, Action<string>? log = null)
    {
        _apChannel = apChannel;

        Projection = new NowPlayingProjection(deviceId);
        Devices = new LiveConnectDevices();
        _ingest = new ClusterIngest(transport, Projection, Devices, deviceId, log);

        var builder = new ConnectStateBuilder(deviceId, "Wavee");
        _connect = new ConnectService(transport, deviceId,
            mid => builder.BuildPutState(mid, isActive: false, Wavee.Protocol.Player.PutStateReason.NewConnection),
            onClusterBytes: _ingest.OnAnnounceResponse, log: log);

        var keySource = new LiveAudioKeySource(() => _apChannel);
        var resolver = new LiveTrackResolver(transport, keySource, log);
        _host = new SilentAudioHost();
        var outbound = new LiveOutboundControl(transport, deviceId);
        // Stage I — play-history telemetry fans off the controller's event log (Recently Played / play counts).
        var telemetry = new TelemetryProjection(new GaboTelemetry(log), () => Projection.ContextUri);
        Controller = new PlaybackController(_host, resolver, Projection,
            resolveContext ?? ((_, _) => Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>())),
            deviceId, outbound, telemetry, log);

        _commands = new ConnectCommandRouter(transport, cmd => Controller.HandleRemoteCommand(cmd), log);
        Devices.TransferHandler = (id, c) => Controller.TransferToAsync(id, c);
    }

    public void Dispose()
    {
        _commands.Dispose();
        _connect.Dispose();
        _ingest.Dispose();
        Controller.Dispose();
        _apChannel?.Dispose();
        Projection.Dispose();
        try { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }
}
