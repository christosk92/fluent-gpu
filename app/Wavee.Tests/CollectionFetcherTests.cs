using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Spotify;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// The live collection fetcher: POST /collection/v2/{paging|delta} → apply items onto the Store's set + hydrate + advance
// the sync token. HTTP faked; the proto request/response shapes are exercised for real.
public class CollectionFetcherTests
{
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);

    [Fact]
    public async Task FetchSet_FullPage_AppliesItems_Hydrates_AndAdvancesRevision()
    {
        var store = new InMemoryStore();
        var page = new Col.PageResponse { SyncToken = "tok-1", NextPageToken = "" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:b", AddedAt = 2 });

        HttpReq? captured = null;
        var hydrated = new List<string>();
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, _) => { captured = req; return Ok(page.ToByteArray()); });
        var fetcher = new CollectionFetcher(http, () => "https://spclient.test", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r,
            (uris, ct) => { hydrated.AddRange(uris); return Task.CompletedTask; });

        await fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken);

        Assert.Equal("POST", captured!.Method);
        Assert.Contains("/collection/v2/paging", captured.Url);
        Assert.True(store.IsSaved("albums", "spotify:album:a"));
        Assert.True(store.IsSaved("albums", "spotify:album:b"));
        Assert.Equal("tok-1", revs["albums"]);
        Assert.Equal(2, hydrated.Count);

        var sent = Col.PageRequest.Parser.ParseFrom(captured.Body);   // the request body is a real PageRequest
        Assert.Equal("bob", sent.Username);
        Assert.Equal("albums", sent.Set);
    }

    [Fact]
    public async Task FetchSet_Delta_WhenPriorTokenPresent_AddsAndRemoves()
    {
        var store = new InMemoryStore();
        store.SetSaved("liked", "spotify:track:old", true, SyncState.Confirmed);
        var delta = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-2" };
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:new", AddedAt = 9 });
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:old", IsRemoved = true });

        HttpReq? captured = null;
        var revs = new Dictionary<string, string?> { ["liked"] = "tok-1" };   // prior token → delta path
        var http = new FakeExchange((req, _) => { captured = req; return Ok(delta.ToByteArray()); });
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask);

        await fetcher.FetchSetAsync("liked", TestContext.Current.CancellationToken);

        Assert.Contains("/collection/v2/delta", captured!.Url);
        Assert.True(store.IsSaved("liked", "spotify:track:new"));
        Assert.False(store.IsSaved("liked", "spotify:track:old"));
        Assert.Equal("tok-2", revs["liked"]);

        var sent = Col.DeltaRequest.Parser.ParseFrom(captured.Body);
        Assert.Equal("collection", sent.Set);        // liked → wire set name "collection"
        Assert.Equal("tok-1", sent.LastSyncToken);
    }
}
