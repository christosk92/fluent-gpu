using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// LIVE metadata round-trip: login (AP) -> login5 (spclient access token) + client-token (attestation) -> POST spclient
// extended-metadata for ONE uri -> project into the Store -> print. The metadata equivalent of --spotify-login: end-to-end
// proof the whole chain works. Needs creds + network, so the USER runs it (`--spotify-metadata spotify:track:...`).
public static class SpotifyMetadataProbe
{
    const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    public static async Task<int> RunAsync(string uri, Action<string> log, CancellationToken ct)
    {
        var login = await SpotifyLiveLogin.LoginAsync(log, ct).ConfigureAwait(false);
        if (login is null) return 1;
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
        catch (Exception ex) { log("login5 failed: " + ex.Message); return 1; }
        log("  access token obtained (expires " + access.ExpiresAt.ToString("u") + ").");

        // 3. resolve an spclient host.
        var spJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=spclient", ct).ConfigureAwait(false);
        var spHosts = ApResolver.ParseHosts(spJson, "spclient");
        if (spHosts.Count == 0) { log("No spclient hosts returned."); return 1; }
        string baseUrl = "https://" + spHosts[0].Split(':')[0];
        log("spclient: " + baseUrl);

        // 4. pipeline: bearer (Auth) + client-token & desktop headers (ClientToken) + 429-backoff (RateLimit) over the real exchange.
        string accessToken = access.Token;
        var pipeline = new HttpPipeline(
            new HttpClientExchange(),
            new AuthMiddleware((force, _) => Task.FromResult(accessToken)),
            new ClientTokenMiddleware(_ => Task.FromResult(clientToken)),
            new RateLimitMiddleware());

        // 5. wire the metadata chain (a one-shot InMemoryStore — no persistence needed for the probe).
        var tier = welcome.Product?.IsPremium == true ? Tier.Premium : Tier.Free;
        var session = new SessionContext(welcome.Username, welcome.Country ?? "US",
            tier == Tier.Premium ? "premium" : "free", "en", tier, false);
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(pipeline, () => baseUrl, () => session);
        var metadata = new MetadataService(source, store, () => session);

        // 6. fetch + print.
        log("Fetching extended-metadata for " + uri + " ...");
        try { await metadata.EnsureAsync(uri).ConfigureAwait(false); }
        catch (Exception ex) { log("extended-metadata fetch failed: " + ex.Message); return 1; }
        PrintEntity(uri, store, log);
        return 0;
    }

    static void PrintEntity(string uri, IStore store, Action<string> log)
    {
        switch (EntityRef.Parse(uri).Kind)
        {
            case EntityKind.Track when store.GetTrack(uri) is { } t:
                log("  TRACK: " + t.Title + " - " + string.Join(", ", t.Artists.Select(a => a.Name)) + " [" + t.Album.Name + "] " + t.DurationMs + "ms");
                break;
            case EntityKind.Album when store.GetAlbum(uri) is { } al:
                log("  ALBUM: " + al.Name + " - " + string.Join(", ", al.Artists.Select(a => a.Name)) + " (" + al.Year + ", " + al.TrackCount + " tracks)");
                break;
            case EntityKind.Artist when store.GetArtist(uri) is { } ar:
                log("  ARTIST: " + ar.Name);
                break;
            default:
                log("  (entity not found in the Store after the fetch - unexpected URI kind or empty response)");
                break;
        }
    }
}
