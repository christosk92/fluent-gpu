using System.Buffers;
using System.Collections.Concurrent;
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
/// <para>Occupancy bookkeeping: the directory is enumerated ONCE per session, on a background task started at first
/// use, which publishes the seeded byte/object totals; every write and delete adds to a running delta on top. Until
/// the seed lands, trimming is deferred (the delta still accumulates). Once seeded, exceeding either budget schedules
/// one background trim pass that evicts oldest-first down to 90 % of the budgets, at most
/// <c>maxEvictionsPerTrim</c> files per pass (bounded work; a pass that hits the cap chains the next one).</para>
/// <para>Recency is an in-memory touch map (file name → UTC tick of the last hit or write this session) — a hit is
/// a pure read, never a metadata write. A file never touched this session orders by its on-disk
/// <c>LastWriteTimeUtc</c>, so a restart still evicts the oldest content first.</para>
/// </summary>
public sealed class DiskImageCache
{
    /// <summary>Upper bound on the url → file-name memo; past it the memo is cleared and rebuilt on demand (bounded
    /// memory without eviction bookkeeping — a cleared entry only costs one SHA-256 to recompute).</summary>
    private const int MaxNameMemo = 8192;
    /// <summary>Hysteresis: a trim evicts down to this fraction of the byte and object budgets so the next writes do
    /// not immediately re-trigger it.</summary>
    private const double TrimTargetFraction = 0.9;

    private readonly string _dir;
    private readonly long _budgetBytes;
    private readonly int _maxObjects;
    private readonly TimeSpan _maxAge;
    private readonly int _maxEvictionsPerTrim;
    private readonly object _trimLock = new();

    // url → (hex file name, full path): the SHA-256 + hex string is computed once per url per session.
    private readonly ConcurrentDictionary<string, (string Name, string FullPath)> _names = new(StringComparer.Ordinal);
    // file name → DateTime.UtcNow.Ticks of the last hit/write this session. Trim orders by this and falls back to the
    // file's LastWriteTimeUtc for names absent here; entries leave when their file does.
    private readonly ConcurrentDictionary<string, long> _touch = new(StringComparer.Ordinal);

    // Occupancy = seeded directory totals + this session's running delta. Both approximate (a write racing the seed
    // enumeration may be counted twice, an overwrite does not subtract the previous size); every trim pass
    // re-enumerates and republishes exact totals, so the error is transient and only ever trims EARLIER.
    private int _seedState;                 // 0 idle, 1 enumerating, 2 published
    private readonly TaskCompletionSource _seeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seededBytes;
    private long _seededCount;
    private long _deltaBytes;
    private long _deltaCount;
    private int _trimQueued;                // 0/1: at most one background trim pass in flight

    public DiskImageCache(string? directory = null, long budgetBytes = 256L << 20, int maxObjects = 4096, TimeSpan? maxAge = null,
                          int maxEvictionsPerTrim = 256)
    {
        _dir = directory ?? Path.Combine(Path.GetTempPath(), "fluent-gpu", "imgcache");
        _budgetBytes = budgetBytes;
        _maxObjects = maxObjects;
        _maxAge = maxAge ?? TimeSpan.FromDays(30);   // Flutter's default stale period
        _maxEvictionsPerTrim = Math.Max(1, maxEvictionsPerTrim);
        try { Directory.CreateDirectory(_dir); } catch { /* read-only FS → cache silently disabled */ }
    }

    /// <summary>Approximate bytes on disk: the seeded total plus this session's delta (exact right after a trim pass).</summary>
    internal long ApproxBytes => Volatile.Read(ref _seededBytes) + Volatile.Read(ref _deltaBytes);
    /// <summary>Approximate object count on disk: the seeded count plus this session's delta.</summary>
    internal long ApproxCount => Volatile.Read(ref _seededCount) + Volatile.Read(ref _deltaCount);

    /// <summary>The content-addressed path for <paramref name="url"/> (lower-hex SHA-256 of its UTF-8 bytes), memoized per url.</summary>
    internal string PathFor(string url) => Resolve(url).FullPath;

    private (string Name, string FullPath) Resolve(string url)
    {
        if (_names.TryGetValue(url, out var entry)) return entry;
        string name = HashName(url);
        entry = (name, Path.Combine(_dir, name));
        if (_names.Count >= MaxNameMemo) _names.Clear();
        _names.TryAdd(url, entry);
        return entry;
    }

    private static string HashName(string url)
    {
        Span<byte> hash = stackalloc byte[32];
        int n = Encoding.UTF8.GetByteCount(url);
        byte[]? rented = n > 512 ? ArrayPool<byte>.Shared.Rent(n) : null;
        Span<byte> utf8 = rented is null ? stackalloc byte[512] : rented;
        int written = Encoding.UTF8.GetBytes(url, utf8);
        SHA256.HashData(utf8[..written], hash);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Hit ⇒ a pooled buffer (the scheduler returns it); miss/stale ⇒ <c>FetchResult.Fail(...)</c> with a
    /// neutral kind so the caller falls through to the network. A hit reads only — recency is recorded in memory.</summary>
    public async Task<FetchResult> TryReadAsync(string url, CancellationToken ct)
    {
        EnsureSeeding();
        var (name, path) = Resolve(url);
        try
        {
            if (!File.Exists(path)) return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, useAsync: true);
            long length = fs.Length;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(fs.SafeFileHandle) > _maxAge)
            {
                fs.Dispose();
                Forget(name, path, length);
                return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound);
            }

            int len = (int)Math.Min(length, int.MaxValue);
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
                fs.Dispose();
                Forget(name, path, length);
                Diag.Count("media", "diskReject");
                return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound);
            }

            Diag.Count("media", "diskHit");
            _touch[name] = DateTime.UtcNow.Ticks;   // LRU recency — in memory, no per-hit metadata write
            return FetchResult.Pooled(buf, got);
        }
        catch (OperationCanceledException) { throw; }
        catch { return FetchResult.Fail(FluentGpu.Scene.ImageFailureKind.NotFound); }
    }

    /// <summary>Persist encoded bytes for <paramref name="url"/> (temp file + atomic move); schedules a background
    /// trim when the seeded occupancy exceeds a budget.</summary>
    public async Task WriteAsync(string url, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        EnsureSeeding();
        var (name, path) = Resolve(url);
        string tmp = string.Concat(path, ".", Environment.CurrentManagedThreadId.ToString("x"), ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 0, useAsync: true))
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            bool existed = File.Exists(path);
            File.Move(tmp, path, overwrite: true);

            Interlocked.Add(ref _deltaBytes, bytes.Length);   // an overwrite over-counts by the old size; the next trim recounts
            if (!existed) Interlocked.Increment(ref _deltaCount);
            _touch[name] = DateTime.UtcNow.Ticks;
            if (OverBudget()) ScheduleTrim();
        }
        catch { TryDelete(tmp); }   // never let a failed write leave a temp file or throw into the worker
    }

    /// <summary>Delete a stale/corrupt entry and take it out of the bookkeeping.</summary>
    private void Forget(string name, string path, long bytes)
    {
        TryDelete(path);
        _touch.TryRemove(name, out _);
        Interlocked.Add(ref _deltaBytes, -bytes);
        Interlocked.Decrement(ref _deltaCount);
    }

    // ---- seeding -------------------------------------------------------------------------------------------------

    /// <summary>Starts the one-time background enumeration on first use; idempotent and lock-free after the first call.</summary>
    private void EnsureSeeding()
    {
        if (Volatile.Read(ref _seedState) != 0) return;
        if (Interlocked.CompareExchange(ref _seedState, 1, 0) != 0) return;
        _ = Task.Run(Seed);
    }

    /// <summary>Test hook: ensures seeding has started and returns the task that completes once the totals are published.</summary>
    internal Task SeedAsync()
    {
        EnsureSeeding();
        return _seeded.Task;
    }

    private void Seed()
    {
        long bytes = 0, count = 0;
        try
        {
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles()) { bytes += f.Length; count++; }
        }
        catch { /* unreadable directory: seed as empty — this session's delta alone bounds the writes */ }
        finally
        {
            Volatile.Write(ref _seededBytes, bytes);
            Volatile.Write(ref _seededCount, count);
            Volatile.Write(ref _seedState, 2);
            _seeded.TrySetResult();
        }
        if (OverBudget()) ScheduleTrim();   // a directory already past budget from a previous session trims now, not at the next write
    }

    private bool OverBudget()
    {
        if (Volatile.Read(ref _seedState) != 2) return false;   // deferred until the directory totals are known
        return ApproxBytes > _budgetBytes || ApproxCount > _maxObjects;
    }

    // ---- trimming ------------------------------------------------------------------------------------------------

    private void ScheduleTrim()
    {
        if (Interlocked.CompareExchange(ref _trimQueued, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            int evicted;
            try { evicted = TrimPass(); }
            finally { Volatile.Write(ref _trimQueued, 0); }
            if (evicted >= _maxEvictionsPerTrim && OverBudget()) ScheduleTrim();   // capped pass: chain the next one
        });
    }

    /// <summary>One bounded eviction pass: enumerates the directory (the only enumeration outside the seed), republishes
    /// exact totals, and when over either budget deletes oldest-first — by this session's touch tick, else the file's
    /// <c>LastWriteTimeUtc</c> — until both totals are at or below 90 % of their budgets or the per-pass cap is reached.
    /// Returns the number of files evicted (0 when a concurrent pass holds the lock).</summary>
    internal int TrimPass() => TrimPass(_budgetBytes, _maxObjects);

    /// <summary><see cref="TrimPass()"/> against explicit budgets (the test seam; production passes the configured ones).</summary>
    internal int TrimPass(long budgetBytes, int maxObjects)
    {
        if (!Monitor.TryEnter(_trimLock)) return 0;   // a concurrent trim is already running
        try
        {
            FileInfo[] files;
            try { files = new DirectoryInfo(_dir).GetFiles(); } catch { return 0; }
            long total = 0;
            foreach (var f in files) total += f.Length;
            int count = files.Length;
            int evicted = 0;

            if (total > budgetBytes || count > maxObjects)
            {
                var recency = new long[files.Length];
                for (int i = 0; i < files.Length; i++)
                    recency[i] = _touch.TryGetValue(files[i].Name, out long tick) ? tick : files[i].LastWriteTimeUtc.Ticks;
                Array.Sort(recency, files);   // oldest first (LRU)

                long byteTarget = (long)(budgetBytes * TrimTargetFraction);
                int countTarget = (int)(maxObjects * TrimTargetFraction);
                for (int i = 0; i < files.Length && evicted < _maxEvictionsPerTrim && (total > byteTarget || count > countTarget); i++)
                {
                    long sz = files[i].Length;
                    try { files[i].Delete(); } catch { continue; }   // an in-flight .tmp (FileShare.None) stays
                    total -= sz; count--; evicted++;
                    _touch.TryRemove(files[i].Name, out _);
                    Diag.Count("media", "diskEvict");
                }
            }

            // Exact totals replace seed + delta (a write racing this enumeration is re-counted by the next pass).
            Interlocked.Exchange(ref _deltaBytes, 0);
            Interlocked.Exchange(ref _deltaCount, 0);
            Volatile.Write(ref _seededBytes, total);
            Volatile.Write(ref _seededCount, count);
            Volatile.Write(ref _seedState, 2);
            _seeded.TrySetResult();
            return evicted;
        }
        catch { return 0; }   // a racing delete/rename mid-pass: give up this pass, the next write re-checks
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
