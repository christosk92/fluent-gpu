using System;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

// ── Stage I — the live play-history sink (gabo-receiver-service) ──────────────────────────────────────────────────────
// Reports each play to Spotify for Recently Played / play counts. The play SIGNAL is captured here from the proto-free
// TelemetryProjection; the full gabo EventEnvelope wire shape — event_sender_envelope + the ~12 playback-event protos
// (audio-session / content-integrity / boombox-session / played-duration segments) + the anti-fraud client/device context
// posted to spclient.wg.spotify.com/gabo-receiver-service/v3/events — is the remaining LIVE-TUNING work (it must be
// validated against the account's Recently Played, which can't be unit-checked). Until that lands this logs the play so the
// pipeline is observable end-to-end; basic Recently Played is already driven by license=premium in our DeviceInfo + the
// PutState player_state (has_been_playing_for_ms).
public sealed class GaboTelemetry : IPlaybackTelemetry
{
    readonly Action<string>? _log;
    public GaboTelemetry(Action<string>? log = null) => _log = log;

    public void ReportPlay(in PlayReport report)
        => _log?.Invoke("play-history: " + report.TrackUri + (report.ContextUri is { Length: > 0 } c ? " (context " + c + ")" : ""));
}
