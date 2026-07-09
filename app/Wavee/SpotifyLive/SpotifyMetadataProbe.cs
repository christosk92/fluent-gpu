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
    public static async Task<int> RunAsync(string uri, WaveeLogger log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct).ConfigureAwait(false);
        if (live is null) return 1;

        // wire the metadata chain (a one-shot InMemoryStore — no persistence needed for the probe).
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
        var metadata = new MetadataService(source, store, () => live.Session);

        log.Info("Fetching extended-metadata for " + uri + " ...");
        try { await metadata.EnsureAsync(uri).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("extended-metadata fetch failed: " + ex.Message); return 1; }
        PrintEntity(uri, store, log);
        return 0;
    }

    static void PrintEntity(string uri, IStore store, WaveeLogger log)
    {
        switch (EntityRef.Parse(uri).Kind)
        {
            case EntityKind.Track when store.GetTrack(uri) is { } t:
                log.Info("  TRACK: " + t.Title + " - " + string.Join(", ", t.Artists.Select(a => a.Name)) + " [" + t.Album.Name + "] " + t.DurationMs + "ms");
                break;
            case EntityKind.Album when store.GetAlbum(uri) is { } al:
                log.Info("  ALBUM: " + al.Name + " - " + string.Join(", ", al.Artists.Select(a => a.Name)) + " (" + al.Year + ", " + al.TrackCount + " tracks)");
                break;
            case EntityKind.Artist when store.GetArtist(uri) is { } ar:
                log.Info("  ARTIST: " + ar.Name);
                break;
            default:
                log.Info("  (entity not found in the Store after the fetch - unexpected URI kind or empty response)");
                break;
        }
    }
}
