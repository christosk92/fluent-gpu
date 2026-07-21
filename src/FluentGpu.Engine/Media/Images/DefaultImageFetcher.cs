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
/// <item>Streams the body into an <c>ArrayPool</c> buffer — no per-fetch <c>byte[]</c>.</item>
/// </list>
/// Reuse ONE instance app-wide. Maps transport/HTTP-status to <see cref="ImageFailureKind"/> for transient-vs-permanent.
/// </summary>
public sealed class DefaultImageFetcher : IImageFetcher, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DiskImageCache? _disk;
    private readonly string _accept;

    public DefaultImageFetcher(HttpClient? http = null, DiskImageCache? diskCache = null, string? acceptHeader = null)
    {
        _ownsHttp = http is null;
        _http = http ?? CreateClient();
        _disk = diskCache;
        // Safe default: never advertise a format WIC may lack a codec for (avoids an undecodable response).
        // Pass "image/avif,image/webp,image/*" to opt into modern formats when the platform has the codecs.
        _accept = acceptHeader ?? "image/jpeg,image/png,image/*;q=0.5";
    }

    private static HttpClient CreateClient()
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
        return new HttpClient(handler, disposeHandler: true)
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
                    >= 500 => ImageFailureKind.ServerError,   // transient → retried
                    _ => ImageFailureKind.HttpError,          // other 4xx → permanent
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

    // Read a response/file body fully into a single ArrayPool buffer (grown by doubling if the length hint was short).
    // No per-fetch byte[] allocation in the steady state — the pool reuses the buffer across fetches.
    private static async Task<FetchResult> ReadAllPooled(Stream s, long? hint, CancellationToken ct)
    {
        int cap = (int)Math.Clamp(hint ?? 64 * 1024, 4096, 64 * 1024 * 1024);
        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int len = 0;
        try
        {
            while (true)
            {
                if (len == buf.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, len);
                    ArrayPool<byte>.Shared.Return(buf);
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
            ArrayPool<byte>.Shared.Return(buf);   // never leak the rented buffer on a mid-stream error/cancel
            throw;
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
