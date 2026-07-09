using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

public readonly record struct ExtensionKey(string Uri, Xm.ExtensionKind Kind);

public sealed record CachedExtension(
    string Uri,
    Xm.ExtensionKind Kind,
    string? Etag,
    long OfflineTtlSeconds,
    ByteString? Payload,
    bool Missing)
{
    static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);
    static readonly TimeSpan MissingTtl = TimeSpan.FromHours(24);

    public TimeSpan Ttl => Missing
        ? MissingTtl
        : OfflineTtlSeconds > 0 ? TimeSpan.FromSeconds(Math.Clamp(OfflineTtlSeconds, 60, 86_400)) : DefaultTtl;

    public static CachedExtension MissingValue(string uri, Xm.ExtensionKind kind, string? etag = null, long offlineTtlSeconds = 0)
        => new(uri, kind, etag, offlineTtlSeconds, null, Missing: true);
}

public sealed class ExtensionEtagCache
{
    readonly ExtendedMetadataSource _source;
    readonly Resource<ExtensionKey, CachedExtension> _resource;
    readonly SemaphoreSlim _batchGate = new(1, 1);

    public ExtensionEtagCache(ExtendedMetadataSource source, Func<SessionContext> ctx, WaveeLogger log = default, int maxEntries = 2048)
    {
        _source = source;
        _resource = new Resource<ExtensionKey, CachedExtension>(
            async (key, _) =>
            {
                var fetched = await FetchBatchAsync(new[] { key }, CancellationToken.None).ConfigureAwait(false);
                return fetched.TryGetValue(key, out var value) ? value : CachedExtension.MissingValue(key.Uri, key.Kind);
            },
            new FreshnessPolicy.Etag(TimeSpan.FromHours(6)),
            ctx,
            ttlOf: x => x.Ttl,
            maxEntries: maxEntries,
            name: "extended-metadata",
            debugLog: log);
    }

    public int FetchCount => _resource.FetchCount;

    public void MarkStale(string uri, Xm.ExtensionKind kind)
        => _resource.MarkStale(new ExtensionKey(uri, kind));

    public async Task<ByteString?> GetPayloadAsync(string uri, Xm.ExtensionKind kind, CancellationToken ct = default)
    {
        var values = await GetAsync(new[] { (uri, kind) }, ct).ConfigureAwait(false);
        return values.TryGetValue((uri, kind), out var cached) && !cached.Missing ? cached.Payload : null;
    }

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>> GetPayloadsAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default)
    {
        var cached = await GetAsync(requests, ct).ConfigureAwait(false);
        var result = new Dictionary<(string, Xm.ExtensionKind), ByteString>(cached.Count);
        foreach (var (key, value) in cached)
            if (!value.Missing && value.Payload is { IsEmpty: false } payload)
                result[key] = payload;
        return result;
    }

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> GetAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return new Dictionary<(string, Xm.ExtensionKind), CachedExtension>();

        var keys = Normalize(requests);
        var values = new Dictionary<(string, Xm.ExtensionKind), CachedExtension>(keys.Count);
        var misses = new List<ExtensionKey>();

        foreach (var key in keys)
        {
            var cached = _resource.Peek(key);
            if (cached.IsReady && !cached.IsStale && cached.Value is { } value)
            {
                values[(key.Uri, key.Kind)] = value;
                continue;
            }
            misses.Add(key);
        }

        if (misses.Count > 0)
        {
            await _batchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                misses.Clear();
                foreach (var key in keys)
                {
                    var cached = _resource.Peek(key);
                    if (cached.IsReady && !cached.IsStale && cached.Value is { } value)
                    {
                        values[(key.Uri, key.Kind)] = value;
                        continue;
                    }
                    misses.Add(key);
                }

                if (misses.Count > 0)
                {
                    var fetched = await FetchBatchAsync(misses, ct).ConfigureAwait(false);
                    foreach (var key in misses)
                    {
                        var value = fetched.TryGetValue(key, out var cached)
                            ? cached
                            : CachedExtension.MissingValue(key.Uri, key.Kind);
                        _resource.Seed(key, value);
                        values[(key.Uri, key.Kind)] = value;
                    }
                }
            }
            finally
            {
                _batchGate.Release();
            }
        }

        return values;
    }

    async Task<IReadOnlyDictionary<ExtensionKey, CachedExtension>> FetchBatchAsync(
        IReadOnlyList<ExtensionKey> keys, CancellationToken ct)
    {
        var reqs = new (string Uri, Xm.ExtensionKind Kind, string? Etag)[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var cached = _resource.Peek(keys[i]);
            reqs[i] = (keys[i].Uri, keys[i].Kind, cached.IsReady ? cached.Value.Etag : null);
        }

        var response = await _source.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        var result = new Dictionary<ExtensionKey, CachedExtension>(keys.Count);
        foreach (var key in keys)
        {
            response.TryGetValue((key.Uri, key.Kind), out var wire);
            var existing = _resource.Peek(key);
            result[key] = Fold(key, existing.IsReady ? existing.Value : null, wire);
        }
        return result;
    }

    static CachedExtension Fold(ExtensionKey key, CachedExtension? existing, ExtendedMetadataSource.ExtensionResult wire)
    {
        return wire.Status switch
        {
            200 when wire.Payload is { IsEmpty: false } payload =>
                new CachedExtension(key.Uri, key.Kind, wire.Etag ?? existing?.Etag, wire.OfflineTtlSeconds, payload, Missing: false),
            304 when existing is not null =>
                existing with
                {
                    Etag = wire.Etag ?? existing.Etag,
                    OfflineTtlSeconds = wire.OfflineTtlSeconds > 0 ? wire.OfflineTtlSeconds : existing.OfflineTtlSeconds,
                },
            404 => CachedExtension.MissingValue(key.Uri, key.Kind, wire.Etag ?? existing?.Etag, wire.OfflineTtlSeconds),
            200 => CachedExtension.MissingValue(key.Uri, key.Kind, wire.Etag ?? existing?.Etag, wire.OfflineTtlSeconds),
            _ when existing is not null => existing,
            _ => CachedExtension.MissingValue(key.Uri, key.Kind),
        };
    }

    static List<ExtensionKey> Normalize(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests)
    {
        var keys = new List<ExtensionKey>(requests.Count);
        var seen = new HashSet<ExtensionKey>();
        foreach (var (uri, kind) in requests)
        {
            if (string.IsNullOrEmpty(uri) || kind == Xm.ExtensionKind.UnknownExtension) continue;
            var key = new ExtensionKey(uri, kind);
            if (seen.Add(key)) keys.Add(key);
        }
        return keys;
    }
}
