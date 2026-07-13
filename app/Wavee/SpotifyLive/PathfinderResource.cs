using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

public readonly record struct PathfinderKey(
    string OperationName,
    string Sha256Hash,
    string BodyHash,
    PathfinderClient.Platform Platform);

public sealed class PathfinderResource
{
    readonly PathfinderClient _client;
    readonly Resource<PathfinderKey, CachedJson> _resource;
    readonly ConcurrentDictionary<PathfinderKey, byte[]> _bodies = new();

    readonly record struct CachedJson(byte[] Bytes, TimeSpan Ttl);

    public PathfinderResource(PathfinderClient client, Func<SessionContext> ctx, WaveeLogger log = default, int maxEntries = 128)
    {
        _client = client;
        _resource = new Resource<PathfinderKey, CachedJson>(
            FetchAsync,
            new FreshnessPolicy.PollWhole(TimeSpan.FromMinutes(15), SuspendInPlayback: false),
            ctx,
            ttlOf: x => x.Ttl,
            maxEntries: maxEntries,
            name: "pathfinder",
            debugLog: log);
    }

    public int FetchCount => _resource.FetchCount;

    public async Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var bytes = await GetBytesAsync(operationName, sha256Hash, writeVariables, platform, ct, ttl).ConfigureAwait(false);
        return bytes is null ? null : JsonDocument.Parse(bytes);
    }

    public async Task<JsonDocument?> UseQueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var ttlValue = ttl ?? TtlFor(operationName);
        if (ttlValue <= TimeSpan.Zero)
            return await QueryAsync(operationName, sha256Hash, writeVariables, platform, ct, ttlValue).ConfigureAwait(false);

        var key = BuildKey(operationName, sha256Hash, writeVariables, platform, out var body);
        _bodies[key] = body;
        var loaded = _resource.Use(key);
        if (loaded.IsReady && loaded.Value is { } cached)
            return JsonDocument.Parse(cached.Bytes);

        return await QueryAsync(operationName, sha256Hash, writeVariables, platform, ct, ttlValue).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetBytesAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var ttlValue = ttl ?? TtlFor(operationName);
        var key = BuildKey(operationName, sha256Hash, writeVariables, platform, out var body);

        if (ttlValue <= TimeSpan.Zero)
            return await _client.QueryBodyBytesAsync(operationName, body, platform, ct).ConfigureAwait(false);

        _bodies[key] = body;
        var loaded = await _resource.GetAsync(key, ct).ConfigureAwait(false);
        return loaded.IsReady ? loaded.Value.Bytes : null;
    }

    async Task<CachedJson> FetchAsync(PathfinderKey key, SessionContext ctx)
    {
        _ = ctx;
        if (!_bodies.TryGetValue(key, out var body))
            throw new InvalidOperationException("missing cached Pathfinder request body");

        try
        {
            var bytes = await _client.QueryBodyBytesAsync(key.OperationName, body, key.Platform, CancellationToken.None)
                .ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException("Pathfinder returned no payload for " + key.OperationName);
            return new CachedJson(bytes, TtlFor(key.OperationName));
        }
        finally
        {
            _bodies.TryRemove(key, out _);
        }
    }

    static PathfinderKey BuildKey(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform, out byte[] body)
    {
        body = PathfinderClient.BuildBody(operationName, sha256Hash, writeVariables);
        var hash = Convert.ToHexString(SHA256.HashData(body));
        return new PathfinderKey(operationName, sha256Hash, hash, platform);
    }

    public static TimeSpan TtlFor(string operationName) => operationName switch
    {
        PathfinderOps.Home => TimeSpan.FromMinutes(15),
        PathfinderOps.GetAlbum => TimeSpan.FromMinutes(10),
        PathfinderOps.GetTrack => TimeSpan.FromMinutes(10),
        PathfinderOps.SimilarAlbumsBasedOnThisTrack => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryAlbumMerch => TimeSpan.FromHours(1),
        PathfinderOps.QueryNpvArtist => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryArtistOverview => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryWhatsNewFeed => TimeSpan.FromMinutes(5),
        _ when operationName.StartsWith("search", StringComparison.OrdinalIgnoreCase) => TimeSpan.Zero,
        _ => TimeSpan.FromMinutes(10),
    };
}
