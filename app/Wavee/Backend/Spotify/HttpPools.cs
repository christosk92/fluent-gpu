using System.Net;
using System.Net.Http;

namespace Wavee.Backend.Spotify;

public enum HttpPool { ControlPlane, Cdn, ThirdParty }

public static class HttpPools
{
    static readonly Lazy<HttpClient> ControlPlane = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(2),
        idleTimeout: TimeSpan.FromMinutes(1),
        maxConnectionsPerServer: 10,
        timeout: TimeSpan.FromSeconds(30),
        preferHttp2: true));

    static readonly Lazy<HttpClient> Cdn = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(5),
        idleTimeout: TimeSpan.FromMinutes(5),
        maxConnectionsPerServer: 16,
        timeout: TimeSpan.FromSeconds(30),
        preferHttp2: true));

    static readonly Lazy<HttpClient> ThirdParty = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(2),
        idleTimeout: TimeSpan.FromMinutes(1),
        maxConnectionsPerServer: 4,
        timeout: TimeSpan.FromSeconds(15),
        preferHttp2: false));

    public static HttpClient Get(HttpPool pool) => pool switch
    {
        HttpPool.Cdn => Cdn.Value,
        HttpPool.ThirdParty => ThirdParty.Value,
        _ => ControlPlane.Value,
    };

    static HttpClient Create(TimeSpan pooledLifetime, TimeSpan idleTimeout, int maxConnectionsPerServer,
        TimeSpan timeout, bool preferHttp2)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledLifetime,
            PooledConnectionIdleTimeout = idleTimeout,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout,
        };
        if (preferHttp2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        return client;
    }
}
