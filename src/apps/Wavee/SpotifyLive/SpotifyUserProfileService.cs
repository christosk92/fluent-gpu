using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Session-scoped Spotify user profile cache for playlist owner / added-by ids.</summary>
sealed class SpotifyUserProfileService : IUserProfileService
{
    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;
    readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    readonly SimpleEvent<string> _changed = new();

    public SpotifyUserProfileService(ExtendedMetadataSource metadata, IHttpExchange http, Func<string> baseUrl,
        WaveeLogger log = default, ExtensionEtagCache? extensions = null)
    {
        _metadata = metadata;
        _extensions = extensions;
        _http = http;
        _baseUrl = baseUrl;
        _log = log;
    }

    public IObservable<string> Changed => _changed;

    public void Seed(string userUriOrId, Owner? owner)
    {
        var key = UserProfileIds.Normalize(userUriOrId);
        if (key is null) return;
        Store(key, owner, emitValueChanges: false);
    }

    public Owner? Get(string userUriOrId)
    {
        var key = UserProfileIds.Normalize(userUriOrId);
        return key is not null && _cache.TryGetValue(key, out var entry) ? entry.Profile : null;
    }

    public void Prefetch(IEnumerable<string> userUriOrIds)
    {
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in userUriOrIds)
        {
            var key = UserProfileIds.Normalize(raw);
            if (key is null || !seen.Add(key) || _cache.ContainsKey(key)) continue;
            if (_inflight.TryAdd(key, Task.CompletedTask)) pending.Add(key);
        }
        if (pending.Count == 0) return;

        var task = ResolveBatchAsync(pending);
        foreach (var key in pending) _inflight[key] = task;
    }

    async Task ResolveBatchAsync(IReadOnlyList<string> userUris)
    {
        try
        {
            var unresolved = new HashSet<string>(userUris, StringComparer.Ordinal);
            try
            {
                var requests = new (string Uri, Xm.ExtensionKind Kind)[userUris.Count];
                for (int i = 0; i < userUris.Count; i++) requests[i] = (userUris[i], Xm.ExtensionKind.UserProfile);
                IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString> payloads = _extensions is not null
                    ? await _extensions.GetPayloadsAsync(requests, CancellationToken.None).ConfigureAwait(false)
                    : await _metadata.GetExtensionsAsync(requests, CancellationToken.None).ConfigureAwait(false);
                foreach (var uri in userUris)
                {
                    if (!payloads.TryGetValue((uri, Xm.ExtensionKind.UserProfile), out var payload)) continue;
                    var owner = ParseJson(payload, uri);
                    if (owner is null) continue;
                    Store(uri, owner);
                    unresolved.Remove(uri);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Info("USER_PROFILE extended-metadata fetch: " + ex.Message);
            }

            if (unresolved.Count > 0)
            {
                var tasks = new List<Task>(unresolved.Count);
                using var gate = new SemaphoreSlim(4);
                foreach (var uri in unresolved)
                    tasks.Add(ResolveRestAsync(uri, gate));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        finally
        {
            for (int i = 0; i < userUris.Count; i++) _inflight.TryRemove(userUris[i], out _);
        }
    }

    async Task ResolveRestAsync(string userUri, SemaphoreSlim gate)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var username = UserProfileIds.BareId(userUri);
            var url = _baseUrl() + "/user-profile-view/v3/profile/" + Uri.EscapeDataString(username) + "?market=from_token";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
            using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), CancellationToken.None).ConfigureAwait(false);
            if (resp.Status != 200)
            {
                if (resp.Status == 404) Store(userUri, null);
                return;
            }
            using var doc = await JsonDocument.ParseAsync(resp.Body).ConfigureAwait(false);
            Store(userUri, ParseJson(doc.RootElement, userUri));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info("user profile REST fetch: " + ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    void Store(string userUri, Owner? owner, bool emitValueChanges = true)
    {
        var key = UserProfileIds.Normalize(userUri);
        if (key is null) return;
        var next = new Entry(owner);
        bool changed = !_cache.TryGetValue(key, out var prior)
            || !Same(prior.Profile, next.Profile);
        _cache[key] = next;
        if (!changed || owner is null) return;
        if (emitValueChanges || prior.Profile is null) _changed.OnNext(key);
    }

    static bool Same(Owner? a, Owner? b)
        => a is null && b is null
           || a is not null && b is not null
           && string.Equals(a.Id, b.Id, StringComparison.Ordinal)
           && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
           && string.Equals(a.Avatar?.Url, b.Avatar?.Url, StringComparison.Ordinal);

    static Owner? ParseJson(ByteString payload, string canonicalUri)
    {
        if (payload.IsEmpty) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload.ToByteArray());
            return ParseJson(doc.RootElement, canonicalUri);
        }
        catch (JsonException) { return null; }
    }

    static Owner? ParseJson(JsonElement root, string canonicalUri)
    {
        var canonical = UserProfileIds.Normalize(StringValue(root, "uri") ?? canonicalUri)
            ?? UserProfileIds.Normalize(canonicalUri);
        string id = canonical is not null ? UserProfileIds.BareId(canonical) : UserProfileIds.BareId(canonicalUri).ToLowerInvariant();

        string? name = StringValue(root, "name") ?? StringValue(root, "display_name");
        string? avatar = StringValue(root, "image_url") ?? FirstImage(root);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(avatar)) return null;

        return new Owner(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(), string.IsNullOrWhiteSpace(avatar) ? null : new Image(avatar));
    }

    static string? StringValue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    static string? FirstImage(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
            if (StringValue(image, "url") is { Length: > 0 } url) return url;
        return null;
    }

    readonly record struct Entry(Owner? Profile);
}
