using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// ── Live session bootstrap — bring up Connect + playback and swap it into the running app ─────────────────────────────
// Logs in, opens the dealer + the persistent AP channel, builds the full LiveConnect stack, and calls svc.GoLive so the
// UI's PlaybackBridge (bound to the switchable facades) starts reflecting + controlling live playback — with NO UI rebuild.
// Returns null if login/dealer aren't available (the app keeps the in-memory fake backend).
public sealed class LiveSessionHost : IAsyncDisposable
{
    readonly LiveDealerTransport _transport;
    readonly LiveConnect _connect;

    LiveSessionHost(LiveDealerTransport transport, LiveConnect connect) { _transport = transport; _connect = connect; }

    public LiveConnect Connect => _connect;

    public static async Task<LiveSessionHost?> StartAsync(Services svc, Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, retainApChannel: true).ConfigureAwait(false);
        if (live is null) return null;

        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count == 0) { log("no dealer host — live session not started"); live.ApChannel?.Dispose(); return null; }

        // The transport's token provider RE-MINTS on reconnect/expiry (not a captured constant).
        var transport = new LiveDealerTransport(dealerHosts[0].Split(':')[0], live.TokenProvider, live.Pipeline, () => live.BaseUrl, log);
        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, resolveContext: null, log: log);
        transport.Start();
        var liveSession = new LiveSpotifySession(live.Username, live.Session.Tier == Tier.Premium);
        svc.GoLive(connect.Controller, connect.Devices, liveSession);
        log("Live Connect session active — Wavee is a controllable device, mirrors now-playing, and shows the live account.");
        return new LiveSessionHost(transport, connect);
    }

    public ValueTask DisposeAsync()
    {
        _connect.Dispose();
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>CLI demo (`--connect-live`): bring up the live session over a REAL Services and log the now-playing the
    /// bridge sees THROUGH the switchable backend, for ~25 s — proving the fake→live swap end-to-end, headlessly.</summary>
    public static async Task<int> RunAsync(Action<string> log, CancellationToken ct)
    {
        log("Wavee live Connect probe — building the real backend + going live...");
        var svc = Services.CreateReal();
        await using var host = await StartAsync(svc, log, ct).ConfigureAwait(false);
        if (host is null) { log("Live session could not start."); return 1; }

        using var sub = svc.Player.State.Changes.Subscribe(Observers.From<Wavee.Core.IPlaybackState>(s =>
        {
            if (s.CurrentTrack is { } t)
                log("  bridge now-playing: " + t.Title + " — " + (s.IsPlaying ? "playing" : "paused") + " (active=" + (s.ActiveDeviceId ?? "") + ")");
        }));
        log("Listening 25s (control Wavee from your phone's Connect picker to drive commands)...");
        try { await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
