using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wavee;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive.Audio.Runtime;

public enum PlayPlayDownloadStage { Fetching, Downloading, Verifying }

/// <summary>Progress for the network-provisioning flow. <see cref="TotalBytes"/> is the compressed payload size when
/// known (Content-Length or the catalog hint), else null (indeterminate).</summary>
public readonly record struct PlayPlayDownloadProgress(
    PlayPlayDownloadStage Stage,
    long BytesReceived,
    long? TotalBytes);

/// <summary>Fetches the runtime catalog and downloads/verifies/installs a pack. Verification is delegated to the
/// existing <see cref="PlayPlayRuntimeVerifier"/> after an atomic install, so the network path and the folder-pick path
/// converge on identical on-disk trust checks (SHA-256 of the decompressed bytes, PE arch, advisory Authenticode).</summary>
public sealed class PlayPlayRuntimeDownloader
{
    public const string DefaultCatalogUrl = "https://cproducts.dev/r/manifest.json";

    readonly HttpClient _http;
    readonly Action<string>? _log;

    public PlayPlayRuntimeDownloader(HttpClient? http = null, Action<string>? log = null)
    {
        _http = http ?? SharedHttp.Client;
        _log = log;
    }

    /// <summary>Resolve the catalog URL: <c>WAVEE_PLAYPLAY_CATALOG_URL</c> env var, then the settings override, then the
    /// built-in default.</summary>
    public static string ResolveCatalogUrl(IAppSettings? settings)
    {
        if (Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_CATALOG_URL") is { Length: > 0 } env)
            return env.Trim();
        var s = settings?.Get(WaveeSettings.PlaybackRuntimeCatalogUrl);
        if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
        return DefaultCatalogUrl;
    }

    public async Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(string url, CancellationToken ct)
    {
        try
        {
            _log?.Invoke($"catalog: GET {url}");
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { _log?.Invoke($"catalog: HTTP {(int)resp.StatusCode}"); return null; }
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var catalog = await JsonSerializer.DeserializeAsync(body, PlayPlayCatalogJson.Default.PlayPlayRuntimeCatalog, ct).ConfigureAwait(false);
            if (catalog is null) { _log?.Invoke("catalog: parse returned null"); return null; }
            if (catalog.Validate() is { } err) { _log?.Invoke($"catalog: invalid ({err})"); return null; }
            _log?.Invoke($"catalog: {catalog.Packs.Length} pack(s)");
            return catalog;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Invoke($"catalog: {ex.Message}"); return null; }
    }

    /// <summary>Newest supported pack for this arch, plus whether any pack targets a different arch (so the UI can say
    /// "not for your device" instead of "nothing available").</summary>
    public static (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog)
    {
        PlayPlayRuntimeCatalogEntry? best = null;
        bool anyOtherArch = false;
        foreach (var e in catalog.Packs)
        {
            if (e.Validate() is not null) continue;
            if (!e.MatchesProcessArch()) { anyOtherArch = true; continue; }
            if (!PlayPlayRuntimeVerifier.SupportedAlgorithms.Contains(e.AlgorithmVersion, StringComparer.Ordinal)) continue;
            if (best is null || CompareVersion(e.AppVersion, best.AppVersion) > 0) best = e;
        }
        return (best, anyOtherArch);
    }

    /// <summary>All packs installable here (right arch + supported algorithm), newest first — the Advanced version picker.</summary>
    public static IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog)
    {
        var list = new List<PlayPlayRuntimeCatalogEntry>();
        foreach (var e in catalog.Packs)
        {
            if (e.Validate() is not null) continue;
            if (!e.MatchesProcessArch()) continue;
            if (!PlayPlayRuntimeVerifier.SupportedAlgorithms.Contains(e.AlgorithmVersion, StringComparer.Ordinal)) continue;
            list.Add(e);
        }
        list.Sort((a, b) => CompareVersion(b.AppVersion, a.AppVersion));
        return list;
    }

    /// <summary>Download <paramref name="entry"/>, verify the decompressed bytes against its pinned SHA-256, install
    /// atomically into <paramref name="destDir"/>, synthesize the local manifest, and re-verify on disk. Mirrors are
    /// tried in order; a hash mismatch on one advances to the next. Throws <see cref="OperationCanceledException"/> on
    /// cancellation after deleting the partial download.</summary>
    public async Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        string destDir,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (entry.Validate() is { } entryErr)
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.NoSupportedPack, entryErr);

        Directory.CreateDirectory(destDir);
        var destDll = Path.Combine(destDir, PlayPlayRuntimePaths.DllFileName);
        var destManifest = Path.Combine(destDir, PlayPlayRuntimePaths.ManifestFileName);
        var tmpDll = destDll + ".download.tmp";

        Exception? lastError = null;
        bool sawHashMismatch = false;

        foreach (var url in entry.Urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hashHex = await DownloadOneAsync(url, entry, tmpDll, progress, ct).ConfigureAwait(false);
                if (!string.Equals(hashHex, entry.DllSha256, StringComparison.OrdinalIgnoreCase))
                {
                    sawHashMismatch = true;
                    _log?.Invoke($"download: hash mismatch from {url} (expected {entry.DllSha256[..12]}…, got {hashHex[..12]}…)");
                    SafeDelete(tmpDll);
                    continue;
                }

                progress?.Report(new(PlayPlayDownloadStage.Verifying, 0, null));
                File.Move(tmpDll, destDll, overwrite: true);          // atomic swap of the verified DLL
                WriteManifestAtomic(destManifest, entry.ToLocalManifest());
                _log?.Invoke($"download: installed {entry.PackId} → {destDir}");
                // Re-hash on disk + PE arch + advisory Authenticode through the same gate the folder-pick path uses.
                return PlayPlayRuntimeVerifier.VerifyDirectory(destDir, allowUntrustedSignature, _log);
            }
            catch (OperationCanceledException)
            {
                SafeDelete(tmpDll);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log?.Invoke($"download: {url} failed: {ex.Message}");
                SafeDelete(tmpDll);
            }
        }

        if (sawHashMismatch)
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.HashMismatch, "downloaded file failed integrity check");
        return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.PackDownloadFailed, lastError?.Message ?? "download failed");
    }

    /// <summary>Stream one URL to <paramref name="tmpDll"/>, inflating Brotli if needed, hashing the decompressed bytes
    /// incrementally. Returns the lowercase hex SHA-256 of what was written.</summary>
    async Task<string> DownloadOneAsync(
        string url,
        PlayPlayRuntimeCatalogEntry entry,
        string tmpDll,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength ?? entry.DownloadSize;

        await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long received = 0;
        // Count COMPRESSED bytes pulled off the wire (matches Content-Length), even when a BrotliStream sits on top.
        var counter = new CountingReadStream(net, n =>
        {
            received += n;
            progress?.Report(new(PlayPlayDownloadStage.Downloading, received, total));
        });
        Stream source = entry.IsBrotli ? new BrotliStream(counter, CompressionMode.Decompress, leaveOpen: true) : counter;

        await using (var file = new FileStream(tmpDll, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        if (entry.IsBrotli) await source.DisposeAsync().ConfigureAwait(false);

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    static void WriteManifestAtomic(string path, PlayPlayRuntimeManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    static void SafeDelete(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    static int CompareVersion(string a, string b)
    {
        if (long.TryParse(a, out var la) && long.TryParse(b, out var lb)) return la.CompareTo(lb);
        return string.CompareOrdinal(a, b);
    }

    /// <summary>Read-only pass-through that reports each chunk's byte count as it is pulled from the underlying stream.</summary>
    sealed class CountingReadStream : Stream
    {
        readonly Stream _inner;
        readonly Action<int> _onRead;
        public CountingReadStream(Stream inner, Action<int> onRead) { _inner = inner; _onRead = onRead; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) _onRead(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n > 0) _onRead(n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
