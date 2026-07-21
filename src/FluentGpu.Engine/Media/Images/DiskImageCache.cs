using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;

namespace FluentGpu.Media;

/// <summary>
/// Persistent on-disk cache of ENCODED image bytes (the second tier under the in-memory GPU residency cache) — the
/// memory+disk model every mature loader uses (Flutter <c>flutter_cache_manager</c>, Nuke/Kingfisher <c>DataCache</c>,
/// SDWebImage). Content-addressed by a hash of the source URL, with an LRU byte budget + max-object count + stale
/// period. It makes CDN images instant on the second view, survives app restarts, and serves offline. Stores the
/// compressed source (≈tens of KB), NOT the decoded BGRA (which is the memory tier) — so the budget covers thousands
/// of covers.
/// </summary>
public sealed class DiskImageCache
{
    private readonly string _dir;
    private readonly long _budgetBytes;
    private readonly int _maxObjects;
    private readonly TimeSpan _maxAge;
    private readonly object _trimLock = new();
    private long _approxBytes = -1;   // lazily seeded on first trim

    public DiskImageCache(string? directory = null, long budgetBytes = 256L << 20, int maxObjects = 4096, TimeSpan? maxAge = null)
    {
        _dir = directory ?? Path.Combine(Path.GetTempPath(), "fluent-gpu", "imgcache");
        _budgetBytes = budgetBytes;
        _maxObjects = maxObjects;
        _maxAge = maxAge ?? TimeSpan.FromDays(30);   // Flutter's default stale period
        try { Directory.CreateDirectory(_dir); } catch { /* read-only FS → cache silently disabled */ }
    }

    private string PathFor(string url)
    {
        Span<byte> hash = stackalloc byte[32];
        int n = Encoding.UTF8.GetByteCount(url);
        byte[]? rented = n > 512 ? ArrayPool<byte>.Shared.Rent(n) : null;
        Span<byte> utf8 = rented is null ? stackalloc byte[512] : rented;
        int written = Encoding.UTF8.GetBytes(url, utf8);
        SHA256.HashData(utf8[..written], hash);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return Path.Combine(_dir, Convert.ToHexStringLower(hash));
    }

    /// <summary>Hit ⇒ a pooled buffer (the scheduler returns it); miss/stale ⇒ <c>FetchResult.Fail(...)</c> with a
    /// neutral kind so the caller falls through to the network.</summary>
    public async Task<FetchResult> TryReadAsync(string url, CancellationToken ct)
    {
        string path = PathFor(url);
        try
        {
            if (!File.Exists(path)) return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound);
            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > _maxAge) { TryDelete(path); return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound); }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, useAsync: true);
            int len = (int)Math.Min(info.Length, int.MaxValue);
            byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, len));
            int got = 0;
            while (got < len)
            {
                int r = await fs.ReadAsync(buf.AsMemory(got, len - got), ct).ConfigureAwait(false);
                if (r == 0) break;
                got += r;
            }
            if (!LooksLikeImage(buf.AsSpan(0, got)))
            {
                ArrayPool<byte>.Shared.Return(buf);
                TryDelete(path);
                Diag.Count("media", "diskReject");
                return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound);
            }

            Diag.Count("media", "diskHit");
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }   // LRU recency
            return FetchResult.Pooled(buf, got);
        }
        catch (OperationCanceledException) { throw; }
        catch { return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound); }
    }

    /// <summary>Persist encoded bytes for <paramref name="url"/> (temp file + atomic move; opportunistic LRU trim).</summary>
    public async Task WriteAsync(string url, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        string path = PathFor(url);
        string tmp = path + "." + Environment.CurrentManagedThreadId.ToString("x") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 0, useAsync: true))
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);

            long now = Interlocked.Add(ref _approxBytes, bytes.Length);
            if (_approxBytes < 0 || now > _budgetBytes) Trim();
        }
        catch { TryDelete(tmp); }   // never let a failed write leave a temp file or throw into the worker
    }

    private void Trim()
    {
        if (!Monitor.TryEnter(_trimLock)) return;   // a concurrent trim is already running
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles();
            long total = 0;
            foreach (var f in files) total += f.Length;

            if (total <= _budgetBytes && files.Length <= _maxObjects) { Interlocked.Exchange(ref _approxBytes, total); return; }

            Array.Sort(files, static (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));   // oldest first (LRU)
            long target = (long)(_budgetBytes * 0.9);
            int count = files.Length;
            for (int i = 0; i < files.Length && (total > target || count > _maxObjects); i++)
            {
                long sz = files[i].Length;
                try { files[i].Delete(); total -= sz; count--; Diag.Count("media", "diskEvict"); } catch { }
            }
            Interlocked.Exchange(ref _approxBytes, total);
        }
        catch { }
        finally { Monitor.Exit(_trimLock); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>True when <paramref name="data"/> begins with a known image container magic (JPEG/PNG/WebP/GIF).</summary>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;   // JPEG
        if (data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') return true;   // PNG
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F') return true;   // GIF
        return data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'   // WebP
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';
    }
}
