using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Fetches the clear unencrypted head file (~128 KB) for instant-start playback.</summary>
public sealed class HeadFileClient
{
    readonly Resource<string, HeadBytes> _cache;
    readonly Action<string>? _log;

    public sealed record HeadBytes(byte[] Data, float NormalizationGainDb);

    public HeadFileClient(IHttpExchange http, Func<SessionContext> ctx, Action<string>? log = null)
    {
        _log = log;
        _cache = new Resource<string, HeadBytes>(FetchAsync, new FreshnessPolicy.Immutable(), ctx);
        _http = http;
    }

    readonly IHttpExchange _http;

    public async Task<HeadBytes> GetAsync(string fileIdHex, CancellationToken ct = default)
    {
        await _cache.Revalidate(fileIdHex).ConfigureAwait(false);   // awaits the (coalesced) fetch to completion
        var loaded = _cache.Peek(fileIdHex);
        if (loaded.IsReady) return loaded.Value!;
        throw new InvalidOperationException("head file fetch failed: " + (loaded.Error ?? "unknown"));
    }

    async Task<HeadBytes> FetchAsync(string fileIdHex, SessionContext ctx)
    {
        // heads-fa-tls13.spotifycdn.com/head/{fileId} — plain GET, no auth, no Range
        var url = $"https://heads-fa-tls13.spotifycdn.com/head/{fileIdHex.ToLowerInvariant()}";
        var resp = await _http.SendAsync(new HttpReq("GET", url, new Dictionary<string, string>()), CancellationToken.None).ConfigureAwait(false);
        using (resp)
        {
            if (resp.Status != 200) throw new InvalidOperationException($"head fetch {resp.Status}");
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, CancellationToken.None).ConfigureAwait(false);
            var data = ms.ToArray();
            float gain = data.Length > 148 ? BitConverter.ToSingle(data, 144) : 0f;
            return new HeadBytes(data, gain);
        }
    }
}
