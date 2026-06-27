using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// Shared live spclient bring-up: login (AP) -> client-token (attestation) -> login5 (spclient access token) -> resolve an
// spclient host -> the middleware HttpPipeline (bearer + client-token + 429-backoff) + a SessionContext. The metadata and
// library probes build on this. Needs creds + network — the USER runs the probes; only the wire shape is unverifiable here.
public sealed record LiveSpclient(HttpPipeline Pipeline, string BaseUrl, SessionContext Session, string Username, string AccessToken, string DeviceId);

public static class SpotifyLiveSpclient
{
    const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    public static async Task<LiveSpclient?> ConnectAsync(Action<string> log, CancellationToken ct)
    {
        var login = await SpotifyLiveLogin.LoginAsync(log, ct).ConfigureAwait(false);
        if (login is null) return null;
        var welcome = login.Welcome;
        var deviceId = login.DeviceId;

        // 1. client-token (attestation) — required on spclient.
        log("Fetching client-token (attestation)...");
        var clientToken = await new ClientTokenClient(ClientId, deviceId).GetAsync(ct).ConfigureAwait(false);
        log(clientToken is null ? "  client-token: NONE (spclient will likely 403)." : "  client-token obtained.");

        // 2. login5 -> the spclient access token (the device-code OAuth token is a Web-API audience, not spclient).
        log("Minting an spclient access token via login5...");
        Login5Client.AccessToken access;
        try
        {
            access = await new Login5Client(ClientId, deviceId)
                .GetAccessTokenAsync(welcome.Username, welcome.ReusableCredentials, clientToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { log("login5 failed: " + ex.Message); return null; }
        log("  access token obtained (expires " + access.ExpiresAt.ToString("u") + ").");

        // 3. resolve an spclient host.
        var spJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=spclient", ct).ConfigureAwait(false);
        var spHosts = ApResolver.ParseHosts(spJson, "spclient");
        if (spHosts.Count == 0) { log("No spclient hosts returned."); return null; }
        string baseUrl = "https://" + spHosts[0].Split(':')[0];
        log("spclient: " + baseUrl);

        // 4. the middleware pipeline over the real exchange.
        string accessToken = access.Token;
        var pipeline = new HttpPipeline(
            new HttpClientExchange(),
            new AuthMiddleware((force, _) => Task.FromResult(accessToken)),
            new ClientTokenMiddleware(_ => Task.FromResult(clientToken)),
            new RateLimitMiddleware());

        var tier = welcome.Product?.IsPremium == true ? Tier.Premium : Tier.Free;
        var session = new SessionContext(welcome.Username, welcome.Country ?? "US",
            tier == Tier.Premium ? "premium" : "free", "en", tier, false);
        return new LiveSpclient(pipeline, baseUrl, session, welcome.Username, accessToken, deviceId);
    }
}
