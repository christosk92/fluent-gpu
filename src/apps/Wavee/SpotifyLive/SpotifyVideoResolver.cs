using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

/// <summary>
/// Resolves a Spotify music-video <c>manifest_id</c> to a parsed <see cref="SpotifyVideoManifest"/> over the app's
/// authenticated transport: <c>GET /manifests/v9/json/sources/{manifestId}/options/supports_drm</c> with the CORS-fenced
/// xpui <c>Origin</c>/<c>Referer</c> (the same request shape <see cref="Audio.AudioFormatProbe"/> already proves). The
/// <c>manifest_id</c> itself is <c>Convert.ToHexStringLower(videoTrack.OriginalVideo[0].Gid)</c> — resolved by the video
/// service from a track's video association (a follow-up factoring of the probe's discovery logic).
/// </summary>
static class SpotifyVideoResolver
{
    const string Xpui = "https://xpui.app.spotify.com";

    public static async Task<SpotifyVideoManifest?> ResolveManifestAsync(ITransport transport, string manifestId, CancellationToken ct = default)
    {
        if (transport is null || string.IsNullOrWhiteSpace(manifestId)) return null;
        string route = "/manifests/v9/json/sources/" + manifestId + "/options/supports_drm";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "*/*",
            ["Origin"] = Xpui,
            ["Referer"] = Xpui + "/",
        };

        Resp resp;
        try
        {
            resp = await transport.Request(Channel.Spclient, route, default, ct, method: "GET", headers: headers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return null;

        try { return SpotifyVideoManifest.FromJson(Encoding.UTF8.GetString(resp.Body)); }
        catch { return null; }   // a malformed/absent manifest → no video (never a throw into playback)
    }
}
