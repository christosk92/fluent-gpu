using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// LIVE library/playlist round-trips — the L1 acceptance probes. Each builds the real spclient pipeline, runs a fetcher
// (the same code the app uses), and prints the result. Needs creds + network, so the USER runs them:
//   --spotify-playlist spotify:playlist:<id>   --spotify-rootlist   --spotify-collection [liked|albums|artists|shows|episodes]
public static class SpotifyLibraryProbe
{
    public static async Task<int> RunPlaylistAsync(string uri, Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var metadata = new MetadataService(new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session), store, () => live.Session);
        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => metadata.SyncAllAsync(uris, c), () => live.Username);

        log("Fetching playlist " + uri + " ...");
        try { await fetcher.FetchPlaylistAsync(uri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log("playlist fetch failed: " + ex.Message); return 1; }

        var membership = store.Membership(uri);
        var rev = store.PlaylistRevision(uri);
        var header = store.GetPlaylist(uri);
        log("  name: " + (header?.Name ?? "(none)") + "   revision: " + (rev is null ? "(none)" : System.Convert.ToHexString(rev)));
        log("  " + membership.Count + " items:");
        for (int i = 0; i < membership.Count; i++)
        {
            if (i >= 50) { log("    ... (" + (membership.Count - 50) + " more)"); break; }
            var m = membership[i];
            var t = store.GetTrack(m.ItemUri);
            string by = m.AddedBy is { Length: > 0 } a ? "  (added by " + a + ")" : "";
            log("    " + (i + 1) + ". " + (t is { } tt ? tt.Title + " - " + string.Join(", ", tt.Artists.Select(x => x.Name)) : m.ItemUri) + by);
        }
        return 0;
    }

    public static async Task<int> RunRootlistAsync(Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => Task.CompletedTask, () => live.Username);   // rootlist items are playlist uris

        string rootlistUri = "spotify:user:" + live.Username + ":rootlist";
        log("Fetching rootlist " + rootlistUri + " ...");
        try { await fetcher.FetchRootlistAsync(rootlistUri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log("rootlist fetch failed: " + ex.Message); return 1; }

        var rl = store.Rootlist();
        log("  " + rl.Count + " rootlist entries:");
        foreach (var e in rl)
        {
            string indent = new string(' ', 4 + System.Math.Max(0, e.Depth) * 2);
            string label = e.Kind == 1 ? "[folder] " + (e.GroupName ?? "") : e.Kind == 2 ? "[/folder]" : e.Uri;
            log(indent + label);
        }
        return 0;
    }

    public static async Task<int> RunCollectionAsync(string setId, Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var metadata = new MetadataService(new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session), store, () => live.Session);
        var revs = new Dictionary<string, string?>();
        var fetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (uris, c) => metadata.SyncAllAsync(uris, c));

        log("Fetching collection set '" + setId + "' ...");
        try { await fetcher.FetchSetAsync(setId, ct).ConfigureAwait(false); }
        catch (Exception ex) { log("collection fetch failed: " + ex.Message); return 1; }

        var items = store.SavedUris(setId);
        log("  " + items.Count + " items in '" + setId + "' (sync token " + (revs.GetValueOrDefault(setId) ?? "none") + "):");
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= 50) { log("    ... (" + (items.Count - 50) + " more)"); break; }
            log("    " + (i + 1) + ". " + PrintItem(items[i], store));
        }
        return 0;
    }

    static string PrintItem(string uri, IStore store) => EntityRef.Parse(uri).Kind switch
    {
        EntityKind.Track => store.GetTrack(uri) is { } t ? t.Title + " - " + string.Join(", ", t.Artists.Select(a => a.Name)) : uri,
        EntityKind.Album => store.GetAlbum(uri)?.Name ?? uri,
        EntityKind.Artist => store.GetArtist(uri)?.Name ?? uri,
        EntityKind.Show => store.GetShow(uri)?.Name ?? uri,
        EntityKind.Episode => store.GetEpisode(uri)?.Title ?? uri,
        _ => uri,
    };
}
