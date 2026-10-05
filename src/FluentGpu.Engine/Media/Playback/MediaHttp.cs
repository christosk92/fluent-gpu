using System;
using System.Net.Http;

namespace FluentGpu.Media;

/// <summary>
/// The ONE <see cref="HttpClient"/> the playback stack's cold-path fetches share: sidecar subtitles
/// (<see cref="MediaPlayer"/>), adaptive manifests (<c>MfMediaPlayer</c>) and in-band WebVTT segments
/// (<c>MfMediaSession</c>). Each of those used to hold its own default <c>new HttpClient()</c> — three connection pools to the
/// same CDN hosts, and a handler with no <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>, which keeps a pooled
/// connection (and so its DNS answer) for the life of the process. Requests that need a tighter budget than the client's
/// backstop <see cref="HttpClient.Timeout"/> bound themselves with a linked cancellation token.
/// </summary>
public static class MediaHttp
{
    /// <summary>How long a pooled connection is reused before it is retired, so a long-running player picks up DNS
    /// changes (a CDN failover) instead of pinning the first answer forever.</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    private static readonly Lazy<HttpClient> s_shared = new(static () => new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = PooledConnectionLifetime,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    }));

    /// <summary>The shared client. Never dispose it.</summary>
    public static HttpClient Shared => s_shared.Value;
}
