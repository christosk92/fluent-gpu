using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Scene;

namespace FluentGpu.Media;

/// <summary>
/// HTTP(S) + local-file fetcher, configured per the cross-ecosystem consensus (Flutter cache_manager, iOS URLSession,
/// OkHttp/Coil, browsers):
/// <list type="bullet">
/// <item>ONE pooled <see cref="SocketsHttpHandler"/> — never <c>new HttpClient()</c> per request (socket exhaustion).</item>
/// <item><see cref="SocketsHttpHandler.PooledConnectionLifetime"/> recycles connections so DNS is re-resolved (stale-DNS
///   / CDN-edge rotation fix) without IHttpClientFactory.</item>
/// <item>HTTP/2 with multiple connections — CDN request multiplexing; bounded <c>MaxConnectionsPerServer</c>.</item>
/// <item>Automatic decompression; per-request deadline via the token (NOT the global <c>HttpClient.Timeout</c>).</item>
/// <item>Disk-first: a persistent <see cref="DiskImageCache"/> serves instant, offline, restart-surviving hits.</item>
/// <item>Streams the body into ONE buffer from the fetcher's own pool, sized from <c>Content-Length</c> — no per-fetch
///   <c>byte[]</c>, no rent-copy-return chain, and no dependence on how small the host capped
///   <c>ArrayPool&lt;byte&gt;.Shared</c> (see <see cref="ReadAllPooled"/>).</item>
/// </list>
/// Reuse ONE instance app-wide. Maps transport/HTTP-status to <see cref="ImageFailureKind"/> for transient-vs-permanent.
/// </summary>
public sealed class DefaultImageFetcher : IImageFetcher, IDisposable
{
    // Buffer policy (scroll-feel 2026-09-16 W3-E1). Encoded covers are 50–400 KB, well past the 85 KB LOH threshold, and
    // the app caps ArrayPool<byte>.Shared at 4 partitions × 4 arrays for its own reasons — so a Shared rental that missed
    // the pool was a fresh LOH array, and the old "start at 64 KB, double" loop rented (and dropped) a CHAIN of them per
    // cover: 64 → 128 → 256 KB for a 150 KB JPEG. That worker-thread churn was the gen-2 signature behind cover-heavy
    // scrolls. A dedicated pool sized for encoded images (8 MiB ceiling, 16 arrays per size class — a scroll's worth of
    // in-flight fetches at every bucket) plus one Content-Length-sized rental makes the steady state allocation-free.

    /// <summary>Largest array the dedicated pool retains; a rental above it is served fresh and dropped on return (a
    /// multi-megabyte poster is a one-off, not a scroll workload).</summary>
    internal const int PoolMaxArrayLength = 8 * 1024 * 1024;
    /// <summary>Arrays retained per power-of-two size class — bounds the pool at a few MB per hot bucket while covering
    /// the scheduler's whole worker fan-out (≤ 6 workers × in-flight + just-decoded).</summary>
    internal const int PoolMaxArraysPerBucket = 16;
    /// <summary>Slack added to a <c>Content-Length</c> hint so the buffer has room for the terminating zero-length read:
    /// without it a body whose length is exactly a pool bucket size (65 536, 131 072, …) would be "full" before EOF was
    /// observed and take one needless doubling step.</summary>
    internal const int ContentLengthSlack = 1024;
    /// <summary>First rental when the response carries no length (chunked transfer); grows by doubling.</summary>
    internal const int ChunkedInitialCapacity = 64 * 1024;
    /// <summary>Ceiling on a single body buffer (a length hint beyond it is clamped and the doubling path takes over).</summary>
    internal const int MaxBufferBytes = 64 * 1024 * 1024;
    private const int MinBufferBytes = 4096;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DiskImageCache? _disk;
    private readonly string _accept;
    private volatile ArrayPool<byte> _pool;   // swapped by the idle trim; every rent/return reads the live one
    private readonly bool _ownsPool;          // false for an injected (test) pool: never swapped
    private long _lastUseMs;                  // Environment.TickCount64 of the latest rent
    private int _dirty;                      // the pool has held arrays since it was created / last swapped
    private Timer? _idleTimer;
    internal ArrayPool<byte> PoolForTest => _pool;

    public DefaultImageFetcher(HttpClient? http = null, DiskImageCache? diskCache = null, string? acceptHeader = null)
        : this(http, diskCache, acceptHeader, pool: null) { }

    /// <summary>Test seam: inject the body-buffer pool (a counting wrapper) to observe the rent/return policy.</summary>
    internal DefaultImageFetcher(HttpClient? http, DiskImageCache? diskCache, string? acceptHeader, ArrayPool<byte>? pool)
    {
        _ownsHttp = http is null;
        _http = http ?? CreateClient();
        _disk = diskCache;
        // Safe default: never advertise a format WIC may lack a codec for (avoids an undecodable response).
        // Pass "image/avif,image/webp,image/*" to opt into modern formats when the platform has the codecs.
        _accept = acceptHeader ?? "image/jpeg,image/png,image/*;q=0.5";
        _ownsPool = pool is null;
        _pool = pool ?? ArrayPool<byte>.Create(PoolMaxArrayLength, PoolMaxArraysPerBucket);
    }

    /// <summary>The pool is dropped and re-created once no fetch has rented for this long: a <c>ConfigurableArrayPool</c>
    /// never trims, so the covers of the busiest scroll (up to 16 arrays per size class, LOH arrays up to 8 MiB each)
    /// would otherwise stay resident for the process's life. The next fetch re-rents fresh arrays — the steady state of a
    /// scroll is allocation-free again after its first few covers, and an idle app holds none.</summary>
    internal const long PoolIdleTrimMs = 30_000;

    /// <summary>Pure trim decision: the pool holds arrays (<paramref name="dirty"/>) and nothing rented it for
    /// <see cref="PoolIdleTrimMs"/>. Internal for the policy tests.</summary>
    internal static bool ShouldTrimPool(long nowMs, long lastUseMs, bool dirty) => dirty && nowMs - lastUseMs >= PoolIdleTrimMs;

    private void NoteRent()
    {
        Volatile.Write(ref _lastUseMs, Environment.TickCount64);
        Volatile.Write(ref _dirty, 1);
        if (!_ownsPool || Volatile.Read(ref _idleTimer) is not null) return;
        // One cheap 30 s timer for the fetcher's life, started by the first rent (a fetcher that never fetches has none).
        // Concurrent first rents (decode workers) race here: the loser's timer is disposed, never left rooted.
        var t = new Timer(static o => ((DefaultImageFetcher)o!).TrimIdlePool(Environment.TickCount64), this, PoolIdleTrimMs, PoolIdleTrimMs);
        if (Interlocked.CompareExchange(ref _idleTimer, t, null) is not null) t.Dispose();
    }

    /// <summary>Swap in a fresh pool when <see cref="ShouldTrimPool"/> says so (the timer's body; internal for the tests).
    /// The swap comes first, then the dirty flag is consumed: a rent racing in between re-marks it, so the new pool is trimmed
    /// later rather than the mark being lost.</summary>
    internal bool TrimIdlePool(long nowMs)
    {
        if (!ShouldTrimPool(nowMs, Volatile.Read(ref _lastUseMs), Volatile.Read(ref _dirty) != 0)) return false;
        // A rental still in flight returns into the NEW pool (ReturnBuffer/Return both go through _pool): a pow-2 array of
        // any size class is accepted by a fresh pool, so nothing leaks and nothing throws.
        _pool = ArrayPool<byte>.Create(PoolMaxArrayLength, PoolMaxArraysPerBucket);
        Interlocked.Exchange(ref _dirty, 0);
        return true;
    }

    /// <summary>Hand a <see cref="FetchResult.Buffer"/> back to the dedicated pool (the scheduler's post-decode call).
    /// Tolerates a foreign array the way <c>PixelBufferPool</c> does — a non-power-of-two length is dropped to the GC,
    /// never thrown on the worker. A <see cref="DiskImageCache"/> hit is such a foreign array (it rents from
    /// <c>ArrayPool&lt;byte&gt;.Shared</c>, whose buckets are the same 16·2ⁿ sizes), so a disk-served cover simply
    /// migrates into this pool on return — harmless, and one fewer array pressed through the capped shared pool.</summary>
    public void ReturnBuffer(byte[] buffer)
    {
        if (buffer.Length < 16 || !System.Numerics.BitOperations.IsPow2(buffer.Length)) return;
        _pool.Return(buffer);
        Volatile.Write(ref _dirty, 1);   // a foreign (disk-cache) array migrating into the pool counts as use
    }

    /// <summary>The first rental's requested size: <c>Content-Length</c> + <see cref="ContentLengthSlack"/> when the
    /// response declares a length (one rental, one read loop, no growth), <see cref="ChunkedInitialCapacity"/> when it does
    /// not. Pure, so the sizing rule is unit-tested without a stream.</summary>
    internal static int InitialCapacity(long? contentLength)
        => contentLength is long len
            ? (int)Math.Clamp(len + ContentLengthSlack, MinBufferBytes, MaxBufferBytes)
            : ChunkedInitialCapacity;

    /// <summary>The engine's image client, optionally with its transport wrapped (<c>AppOptions.ImageHttpHandler</c>):
    /// the pooling, HTTP/2 and timeout policy stay the engine's; the wrapper only sees the requests go by.</summary>
    public static HttpClient CreateClient(Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),     // recycle → re-resolve DNS / rotate CDN edges; no socket exhaustion
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = 32,                           // healthy CDN parallelism, not the unbounded int.MaxValue default
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpClient(wrap is null ? handler : wrap(handler), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,                     // the per-request CancellationToken owns the deadline
            DefaultRequestVersion = HttpVersion.Version20,          // prefer HTTP/2 (CDN multiplexing); falls back to 1.1
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async Task<FetchResult> FetchAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source)) return FetchResult.Fail(ImageFailureKind.NotFound);

        bool http = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (http)
        {
            // Disk-first: instant, offline, survives restart — the second-tier cache under the in-memory GPU residency.
            if (_disk is not null)
            {
                var hit = await _disk.TryReadAsync(source, ct).ConfigureAwait(false);
                if (hit.Ok) return hit;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, source);
            req.Headers.TryAddWithoutValidation("Accept", _accept);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                return FetchResult.Fail(code switch
                {
                    404 or 410 => ImageFailureKind.NotFound,
                    408 => ImageFailureKind.Timeout,              // server/proxy gave up waiting → transient, retried
                    429 or >= 500 => ImageFailureKind.ServerError, // throttled burst / server fault → transient, retried
                    _ => ImageFailureKind.HttpError,              // other 4xx → permanent
                });
            }
            using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var fetched = await ReadAllPooled(body, resp.Content.Headers.ContentLength, ct).ConfigureAwait(false);

            if (_disk is not null && fetched.Ok
                && DiskImageCache.LooksLikeImage(fetched.Span))
                await _disk.WriteAsync(source, new ReadOnlyMemory<byte>(fetched.Buffer, 0, fetched.Length), ct).ConfigureAwait(false);
            return fetched;
        }

        // local file (file:// URI or a plain path) — stream into the pool too
        string path = source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(source).LocalPath : source;
        if (!File.Exists(path)) return FetchResult.Fail(ImageFailureKind.NotFound);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, useAsync: true);
        return await ReadAllPooled(fs, fs.Length, ct).ConfigureAwait(false);
    }

    /// <summary>Read a response/file body fully into ONE buffer from the fetcher's pool. With a length hint the single
    /// rental (<see cref="InitialCapacity"/>) holds the whole body plus the EOF read; a chunked body (no hint) or a hint
    /// the server undershot falls back to doubling, returning each outgrown buffer to the same pool. The result's buffer
    /// belongs to the caller until it comes back through <see cref="ReturnBuffer"/>. Internal for the buffer-policy tests.</summary>
    internal async Task<FetchResult> ReadAllPooled(Stream s, long? hint, CancellationToken ct)
    {
        NoteRent();
        byte[] buf = _pool.Rent(InitialCapacity(hint));
        int len = 0;
        try
        {
            while (true)
            {
                if (len == buf.Length)
                {
                    byte[] bigger = _pool.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, len);
                    _pool.Return(buf);
                    buf = bigger;
                }
                int n = await s.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
                if (n == 0) break;
                len += n;
            }
            return FetchResult.Pooled(buf, len);
        }
        catch
        {
            _pool.Return(buf);   // never leak the rented buffer on a mid-stream error/cancel
            throw;
        }
    }

    public void Dispose() { _idleTimer?.Dispose(); if (_ownsHttp) _http.Dispose(); }
}
