using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media.Adaptive;

public sealed class AdaptiveManifestException : Exception
{
    public AdaptiveManifestException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Cold-path manifest fetcher with request mutation, timeout, bounded retry and format detection. Segment
/// downloads use the same NetworkOptions contract; this class deliberately owns no decoder/platform state.</summary>
public static class AdaptiveManifestLoader
{
    public static async ValueTask<AdaptiveManifest> LoadAsync(HttpClient client, AdaptiveSource source,
        NetworkOptions? network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        int retries = Math.Clamp(network?.MaxRetries ?? 2, 0, 8);
        TimeSpan timeout = network?.ConnectTimeout ?? TimeSpan.FromSeconds(15);
        Exception? last = null;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
            try
            {
                var model = new NetworkRequest { Uri = source.ManifestUri };
                model = network?.OnRequest?.Invoke(model) ?? model;
                using var request = new HttpRequestMessage(HttpMethod.Get, model.Uri);
                for (int i = 0; i < model.Headers.Count; i++)
                {
                    var (name, value) = model.Headers[i];
                    request.Headers.TryAddWithoutValidation(name, value);
                }
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                var uri = response.RequestMessage?.RequestUri ?? new Uri(model.Uri, UriKind.Absolute);
                AdaptiveManifestKind kind = Detect(source.Options.ManifestKind, uri, text);
                return kind == AdaptiveManifestKind.Hls
                    ? HlsManifestParser.Parse(text, uri)
                    : DashManifestParser.Parse(text, uri);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { last = new TimeoutException($"Manifest request timed out after {timeout.TotalSeconds:0.#} seconds."); }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or UriFormatException)
            { last = ex; }

            if (attempt < retries)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 150 * (1 << attempt))), cancellationToken).ConfigureAwait(false);
        }
        throw new AdaptiveManifestException($"Could not load adaptive manifest '{source.ManifestUri}' after {retries + 1} attempt(s): {last?.Message}", last);
    }

    public static AdaptiveManifestKind Detect(AdaptiveManifestKind requested, Uri uri, string text)
    {
        if (requested is AdaptiveManifestKind.Dash or AdaptiveManifestKind.Hls) return requested;
        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        if (span.StartsWith("#EXTM3U", StringComparison.Ordinal)) return AdaptiveManifestKind.Hls;
        if (span.StartsWith("<", StringComparison.Ordinal) && span.IndexOf("<MPD", StringComparison.OrdinalIgnoreCase) >= 0)
            return AdaptiveManifestKind.Dash;
        string path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return AdaptiveManifestKind.Hls;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return AdaptiveManifestKind.Dash;
        throw new FormatException("The response is neither an HLS playlist nor a DASH MPD.");
    }
}
