using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class SpotifyUserProfileServiceTests
{
    [Fact]
    public async Task Seed_CurrentUser_SkipsLaterPrefetchForSameCanonicalId()
    {
        var http = new CountingExchange();
        var svc = new SpotifyUserProfileService(null!, http, () => "https://spclient");

        svc.Seed("Spotify", new Owner("spotify", "Spotify", new Image("https://i.scdn.co/image/avatar")));
        svc.Prefetch(["spotify", "Spotify", "spotify:user:SPOTIFY"]);
        await Task.Delay(25);

        var owner = svc.Get("spotify:user:spotify");
        Assert.NotNull(owner);
        Assert.Equal("Spotify", owner!.Name);
        Assert.Equal(0, http.Calls);
    }

    sealed class CountingExchange : IHttpExchange
    {
        public int Calls;

        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResp(404, new Dictionary<string, string>(), []));
        }
    }
}
