using System.Collections.Concurrent;
using Wavee;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Wavee.Backend.Audio;

/// <summary>Chunk-sparse on-disk cache of encrypted CDN body bytes (64 KB-aligned). Stores raw wire bytes only —
/// decrypt-agnostic. LRU eviction by whole fileId pair (.enc + .map).</summary>
public sealed class AudioBodyDiskCache
{
    public const int ChunkBytes = 64 * 1024;
    const byte MapVersion = 1;

    readonly string _dir;
    readonly long _budgetBytes;
    readonly int _maxFiles;
    readonly TimeSpan _maxAge;
    readonly object _trimLock = new();
    long _approxBytes;

    public AudioBodyDiskCache(string directory, long budgetBytes = 4L << 30, int maxFiles = 512, TimeSpan? maxAge = null)
    {
        _dir = directory;
        _budgetBytes = budgetBytes;
        _maxFiles = maxFiles;
        _maxAge = maxAge ?? TimeSpan.FromDays(30);
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    public static AudioBodyDiskCache? FromSettings(IAppSettings settings)
    {
        if (!settings.Get(WaveeSettings.AudioBodyCacheEnabled)) return null;
        long budget = Math.Max(64L << 20, settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes));
        return new AudioBodyDiskCache(DefaultDirectory(), budgetBytes: budget);
    }

    public static string DefaultDirectory()
    {
        try { return Path.Combine(FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder, "audio"); }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache", "audio");
        }
    }

    static string Sanitize(string fileId) => fileId.ToLowerInvariant();

    string EncPath(string fileId) => Path.Combine(_dir, Sanitize(fileId) + ".enc");
    string MapPath(string fileId) => Path.Combine(_dir, Sanitize(fileId) + ".map");

    static FileStream OpenShared(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    /// <summary>Known total body size for <paramref name="fileId"/>, or null if unknown.</summary>
    public long? KnownSize(string fileId)
    {
        try
        {
            var map = ReadMap(fileId);
            return map?.TotalSize;
        }
        catch { return null; }
    }

    public void SetSize(string fileId, long size)
    {
        if (size <= 0) return;
        lock (FileLock(fileId))
        {
            var existing = ReadMap(fileId);
            if (existing?.TotalSize == size) return;
            var map = (existing ?? new MapState(0, Array.Empty<byte>())) with { TotalSize = size };
            WriteMapAtomic(fileId, map);
        }
    }

    public bool TryReadChunk(string fileId, int chunkIndex, Span<byte> dst, out int length)
    {
        length = 0;
        try
        {
            MapState? map;
            lock (FileLock(fileId))
            {
                map = ReadMap(fileId);
                if (map is null || !map.IsPresent(chunkIndex)) return false;
            }

            string enc = EncPath(fileId);
            if (!File.Exists(enc)) return false;

            using var fs = OpenShared(enc, FileMode.Open);
            long offset = (long)chunkIndex * ChunkBytes;
            if (fs.Length < offset + 1) return false;
            fs.Seek(offset, SeekOrigin.Begin);
            int want = Math.Min(dst.Length, ChunkBytes);
            int got = fs.Read(dst[..want]);
            if (got <= 0) return false;
            length = got;
            TouchMap(fileId);
            return true;
        }
        catch { return false; }
    }

    public void WriteChunk(string fileId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        lock (FileLock(fileId))
        {
            string enc = EncPath(fileId);
            using (var fs = OpenShared(enc, FileMode.OpenOrCreate))
            {
                long offset = (long)chunkIndex * ChunkBytes;
                if (fs.Length < offset) fs.SetLength(offset);
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(data);
                fs.Flush(true);
            }

            var map = ReadMap(fileId) ?? new MapState(0, Array.Empty<byte>());
            map = map with { Bitmap = SetBit(map.Bitmap, chunkIndex, true) };
            WriteMapAtomic(fileId, map);
            TouchMap(fileId);
        }

        long added = data.Length;
        long now = Interlocked.Add(ref _approxBytes, added);
        if (_approxBytes < 0 || now > _budgetBytes) Trim();
    }

    public void Invalidate(string fileId)
    {
        lock (FileLock(fileId))
        {
            TryDelete(EncPath(fileId));
            TryDelete(MapPath(fileId));
        }
    }

    public void ClearAll()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.EnumerateFiles(_dir)) TryDelete(f);
            Interlocked.Exchange(ref _approxBytes, 0);
        }
        catch { }
    }

    public long DirectoryBytes()
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    public void SetBudget(long budgetBytes)
    {
        if (budgetBytes <= 0) return;
        // budget is ctor-only for simplicity; re-trim via public TrimToBudget
        TrimToBudget(budgetBytes);
    }

    public long TrimToBudget(long budgetBytes)
    {
        return TrimInternal(budgetBytes, _maxFiles);
    }

    public long Trim() => TrimInternal(_budgetBytes, _maxFiles);

    long TrimInternal(long budgetBytes, int maxFiles)
    {
        if (!Monitor.TryEnter(_trimLock)) return 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            var maps = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(_dir, "*.map"))
                maps.Add(new FileInfo(path));
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(_dir)) total += f.Length;

            if (total <= budgetBytes && maps.Count <= maxFiles)
            {
                Interlocked.Exchange(ref _approxBytes, total);
                return 0;
            }

            maps.Sort(static (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            long freed = 0;
            long target = (long)(budgetBytes * 0.9);
            int count = maps.Count;
            foreach (var map in maps)
            {
                if (total <= target && count <= maxFiles) break;
                string id = Path.GetFileNameWithoutExtension(map.Name);
                string enc = Path.Combine(_dir, id + ".enc");
                long sz = map.Length;
                try { if (File.Exists(enc)) sz += new FileInfo(enc).Length; } catch { }
                TryDelete(enc);
                TryDelete(map.FullName);
                total -= sz;
                freed += sz;
                count--;
            }
            Interlocked.Exchange(ref _approxBytes, Math.Max(0, total));
            return freed;
        }
        catch { return 0; }
        finally { Monitor.Exit(_trimLock); }
    }

    readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    object FileLock(string fileId) => _fileLocks.GetOrAdd(Sanitize(fileId), _ => new object());

    sealed record MapState(long TotalSize, byte[] Bitmap)
    {
        public bool IsPresent(int chunkIndex)
        {
            int bi = chunkIndex >> 3;
            int bit = chunkIndex & 7;
            return bi < Bitmap.Length && (Bitmap[bi] & (1 << bit)) != 0;
        }
    }

    MapState? ReadMap(string fileId)
    {
        string path = MapPath(fileId);
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        if (DateTime.UtcNow - fi.LastWriteTimeUtc > _maxAge) { Invalidate(fileId); return null; }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 13 || bytes[0] != MapVersion) return null;
        long total = BitConverter.ToInt64(bytes, 1);
        int blen = BitConverter.ToInt32(bytes, 9);
        if (bytes.Length < 13 + blen) return null;
        var bitmap = new byte[blen];
        Buffer.BlockCopy(bytes, 13, bitmap, 0, blen);
        return new MapState(total, bitmap);
    }

    void WriteMapAtomic(string fileId, MapState map)
    {
        string path = MapPath(fileId);
        string tmp = path + "." + Environment.CurrentManagedThreadId.ToString("x") + ".tmp";
        var payload = new byte[13 + map.Bitmap.Length];
        payload[0] = MapVersion;
        BitConverter.TryWriteBytes(payload.AsSpan(1), map.TotalSize);
        BitConverter.TryWriteBytes(payload.AsSpan(9), map.Bitmap.Length);
        Buffer.BlockCopy(map.Bitmap, 0, payload, 13, map.Bitmap.Length);
        try
        {
            File.WriteAllBytes(tmp, payload);
            File.Move(tmp, path, overwrite: true);
        }
        catch { TryDelete(tmp); }
    }

    static byte[] SetBit(byte[] bitmap, int chunkIndex, bool on)
    {
        int need = (chunkIndex >> 3) + 1;
        if (bitmap.Length < need)
        {
            var grown = new byte[need];
            Buffer.BlockCopy(bitmap, 0, grown, 0, bitmap.Length);
            bitmap = grown;
        }
        int bi = chunkIndex >> 3;
        int bit = chunkIndex & 7;
        if (on) bitmap[bi] |= (byte)(1 << bit);
        else bitmap[bi] &= (byte)~(1 << bit);
        return bitmap;
    }

    void TouchMap(string fileId)
    {
        try { File.SetLastAccessTimeUtc(MapPath(fileId), DateTime.UtcNow); } catch { }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
