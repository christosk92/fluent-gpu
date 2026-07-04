using System;
using System.Collections.Generic;

using Wavee.Backend.Audio;

namespace Wavee.Backend.Spotify;

// Desktop client-identity constants Spotify expects on spclient requests. Version strings are hardcoded in
// SpotifyRuntimeIdentity until manifest-driven pins land.
public static class SpotifyHeaders
{
    public const string ClientId = "65b708073fc0480ea92a077233ca87bd";     // Spotify's public desktop client id
    public static string ClientVersion => SpotifyRuntimeIdentityHost.Current.ClientVersion;
    public static string AppPlatform => SpotifyRuntimeIdentity.AppPlatform;
    public static string AppVersion => SpotifyRuntimeIdentityHost.Current.AppVersion;
    public static string UserAgent => SpotifyRuntimeIdentityHost.Current.UserAgent;

    // ── §2.7 — the first-party header set for the playlist-v2 MUTATION routes (/…/changes, /…/rootlist/changes) ──
    // The gateway gates these routes on a matching (Spotify-App-Version · App-Platform · User-Agent · spotify-playlist-
    // sync-reason) tuple: a request missing them 200-OKs against a PASSIVE read handler that never mutates state — the
    // silent-no-op class this fixes. Content-Type MUST be x-www-form-urlencoded despite the binary protobuf body (anything
    // else routes to the wrong handler). Bearer + client-token + User-Agent are stamped by the HTTP middleware
    // (AuthMiddleware / ClientTokenMiddleware), so they are NOT duplicated here; App-Platform / Spotify-App-Version are
    // repeated defensively (the middleware overwrites them with the same values). Origin is intentionally omitted — it is
    // not part of the gateway's gating tuple, and the spclient base URL isn't available at this layer (the transport owns
    // URL composition).
    public static Dictionary<string, string> PlaylistV2Mutation()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["App-Platform"] = AppPlatform,
            ["Spotify-App-Version"] = AppVersion,
            ["spotify-playlist-sync-reason"] = "CAk=",
            ["Accept-Language"] = "en",
            ["Cache-Control"] = "no-store",
            ["spotify-accept-geoblock"] = "dummy",
            ["spotify-dsa-mode-enabled"] = "false",
        };

    // ── PlayPlay Step A — POST /playplay/v1/key/{fileIdHex} ───────────────────────────────────────────────────────────
    // Same gateway quirk as playlist-v2 mutations: Content-Type MUST be x-www-form-urlencoded despite a protobuf body.
    // Bearer, client-token, App-Platform, Spotify-App-Version, and User-Agent are stamped by ClientTokenMiddleware.
    public static Dictionary<string, string> PlayPlayKey()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept-Language"] = "en",
        };
}
