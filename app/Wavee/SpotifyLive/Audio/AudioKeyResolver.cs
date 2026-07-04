using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>AP-first + PlayPlay-fallback key resolver with Resource-backed coalescing and a decaying per-file latch.</summary>
public sealed class AudioKeyResolver : IAudioKeySource
{
    readonly IAudioKeySource _ap;
    readonly IPlayPlayKeyDeriver? _playPlay;
    readonly Func<RuntimeAsset?> _runtime;
    readonly ILicenseClient? _license;
    readonly AudioRuntimeStatusService _status;
    readonly Resource<string, CachedKey> _cache;
    readonly ConcurrentDictionary<string, PendingGid> _pendingGids = new();
    readonly ConcurrentDictionary<string, LatchState> _latch = new();
    readonly Action<string>? _log;

    sealed record CachedKey(byte[] Key);
    sealed record PendingGid(byte[] TrackGid);
    sealed class LatchState { public int FailCount; public DateTime NextProbe = DateTime.MinValue; }

    public AudioKeyResolver(
        IAudioKeySource ap,
        IPlayPlayKeyDeriver? playPlay,
        Func<RuntimeAsset?> runtime,
        ILicenseClient? license,
        AudioRuntimeStatusService status,
        Func<SessionContext> ctx,
        Action<string>? log = null)
    {
        _ap = ap;
        _playPlay = playPlay;
        _runtime = runtime;
        _license = license;
        _status = status;
        _log = log;
        _cache = new Resource<string, CachedKey>(FetchKeyAsync, new FreshnessPolicy.Immutable(), ctx);
    }

    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        string fileHex = Convert.ToHexStringLower(fileId.Span);
        string cacheKey = fileHex + ":" + Convert.ToHexStringLower(trackGid.Span);
        _pendingGids[cacheKey] = new PendingGid(trackGid.ToArray());

        if (_latch.TryGetValue(fileHex, out var latch) && DateTime.UtcNow < latch.NextProbe)
        {
            var peek = _cache.Peek(cacheKey);
            if (peek.IsReady) return peek.Value!.Key;
        }

        await _cache.Revalidate(cacheKey).ConfigureAwait(false);
        var loaded = _cache.Peek(cacheKey);
        if (loaded.IsReady) return loaded.Value!.Key;

        var reason = loaded.Error is { Length: > 0 } err && Enum.TryParse<AudioKeyFailureReason>(err, out var r)
            ? r : AudioKeyFailureReason.Network;
        _status.SetKeyFailure(reason, loaded.Error);
        throw new AudioPlaybackException(reason, loaded.Error);
    }

    async Task<CachedKey> FetchKeyAsync(string cacheKey, SessionContext ctx)
    {
        var sep = cacheKey.IndexOf(':');
        var fileHex = sep > 0 ? cacheKey[..sep] : cacheKey;
        var fileId = Convert.FromHexString(fileHex);
        var trackGid = _pendingGids.TryGetValue(cacheKey, out var pg) ? pg.TrackGid : fileId.AsSpan(0, Math.Min(16, fileId.Length)).ToArray();

        try
        {
            var key = await _ap.GetKeyAsync(fileId, trackGid, CancellationToken.None).ConfigureAwait(false);
            if (!key.IsEmpty) { _log?.Invoke($"key {fileHex}: AP path OK"); _status.ClearKeyFailure(); return new CachedKey(key.ToArray()); }
            _log?.Invoke($"key {fileHex}: AP returned empty → PlayPlay fallback");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"key {fileHex}: AP failed ({ex.Message}) → PlayPlay fallback");
            if (!ShouldRetryPlayPlay(fileHex))
            {
                _log?.Invoke($"key {fileHex}: PlayPlay latched from a recent failure → giving up as ApPermanent");
                throw new InvalidOperationException(AudioKeyFailureReason.ApPermanent.ToString());
            }
        }

        var asset = _runtime();
        if (asset is null || _playPlay is null || _license is null)
        {
            var r = _playPlay is null ? AudioKeyFailureReason.NeverProvisioned : AudioKeyFailureReason.ProvisioningUnavailable;
            _log?.Invoke($"key {fileHex}: PlayPlay unavailable → {r} (pack={(asset is not null)}, deriver={(_playPlay is not null)}, license={(_license is not null)})");
            NoteLatch(fileHex);
            throw new InvalidOperationException(r.ToString());
        }

        _log?.Invoke($"key {fileHex}: PlayPlay step A — fetching obfuscated key from spclient");
        var obf = await _license.FetchObfuscatedKeyAsync(fileHex, asset.Config, CancellationToken.None).ConfigureAwait(false);
        if (obf.Reason != AudioKeyFailureReason.None) { _log?.Invoke($"key {fileHex}: obfuscated-key fetch FAILED → {obf.Reason}"); NoteLatch(fileHex); throw new InvalidOperationException(obf.Reason.ToString()); }

        var contentId = fileId.AsSpan(0, Math.Min(16, fileId.Length)).ToArray();
        _log?.Invoke($"key {fileHex}: PlayPlay step B — deriving in x64 host");
        var derive = await _playPlay.DeriveAsync(obf.Key, contentId, asset.Config, asset.PackPath,
            correlationId: fileHex, CancellationToken.None).ConfigureAwait(false);
        if (!derive.Ok) { _log?.Invoke($"key {fileHex}: derive FAILED → {derive.Reason} {derive.Detail}"); NoteLatch(fileHex); throw new InvalidOperationException(derive.Reason.ToString()); }

        _log?.Invoke($"key {fileHex}: PlayPlay path OK");
        _status.ClearKeyFailure();
        return new CachedKey(derive.Key.ToArray());
    }

    bool ShouldRetryPlayPlay(string fileHex) =>
        !_latch.TryGetValue(fileHex, out var s) || DateTime.UtcNow >= s.NextProbe;

    void NoteLatch(string fileHex)
    {
        var s = _latch.GetOrAdd(fileHex, _ => new LatchState());
        var fails = Interlocked.Increment(ref s.FailCount);
        s.NextProbe = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * fails));
    }
}
