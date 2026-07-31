using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wavee;

namespace Wavee.Backend.Audio;

public enum AudioCacheBudgetMode { FixedBytes = 0, DriveShare = 1, Unlimited = 2 }
public enum AudioCacheRelocationMode { Move, StartEmptyDeleteOld, StartEmptyKeepOld }

public readonly record struct AudioBodyCacheStatus(
    string Directory,
    bool Available,
    long Bytes,
    long? BudgetBytes,
    long FreeBytes,
    long ReserveBytes,
    bool WriteEnabled);

/// <summary>
/// Sparse on-disk cache of the original encrypted CDN body. Chunks are committed only after their raw bytes are durable
/// and are verified with SHA-256 on every disk read. Decryption always happens later, in memory.
/// </summary>
public sealed class AudioBodyDiskCache
{
    public const int ChunkBytes = 64 * 1024;
    const int HeaderCoreBytes = 20;             // magic + chunk size + total size + chunk count
    const int HeaderBytes = HeaderCoreBytes + 32;
    const int EntryBytes = 1 + 32;              // committed marker + SHA-256
    const long MinBudgetBytes = 64L << 20;
    const long DefaultFixedBudgetBytes = 32L << 30;
    const long MinimumReserveBytes = 5L << 30;
    static readonly byte[] Magic = "WAC2"u8.ToArray();

    readonly Func<Policy>? _policyProvider;
    readonly WaveeLogger _log;
    readonly object _stateGate = new();
    readonly object _trimLock = new();
    readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> _lastTouch = new(StringComparer.OrdinalIgnoreCase);
    string _staticDirectory;
    long _staticBudget;
    string _activeDirectory = "";
    long _approxBytes;
    bool _scanPending;   // a WarmScan is owed for the current _activeDirectory (guarded by _stateGate)
    int _scanGen;        // bumped on every directory switch so a stale scan can't publish over the new root's count

    readonly record struct Policy(bool WriteEnabled, string Directory, AudioCacheBudgetMode Mode, long FixedBytes, int Percent);
    readonly record struct MapHeader(long TotalSize, int ChunkCount);

    public AudioBodyDiskCache(string directory, long budgetBytes = DefaultFixedBudgetBytes, int maxFiles = 0, TimeSpan? maxAge = null,
        WaveeLogger log = default)
    {
        _log = log;
        _staticDirectory = Path.GetFullPath(directory);
        _staticBudget = Math.Max(MinBudgetBytes, budgetBytes);
        EnsureActiveDirectory(CurrentPolicy().Directory);
    }

    AudioBodyDiskCache(Func<Policy> policyProvider, WaveeLogger log)
    {
        _log = log;
        _policyProvider = policyProvider;
        var policy = CurrentPolicy();
        _staticDirectory = policy.Directory;
        _staticBudget = policy.FixedBytes;
        EnsureActiveDirectory(policy.Directory);
    }

    public static AudioBodyDiskCache FromSettings(IAppSettings settings, WaveeLogger log = default) => new(() =>
    {
        string directory = ResolveDirectory(settings.Get(WaveeSettings.AudioBodyCacheBasePath));
        var mode = (AudioCacheBudgetMode)Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetMode), 0, 2);
        return new Policy(
            settings.Get(WaveeSettings.AudioBodyCacheEnabled),
            directory,
            mode,
            Math.Max(MinBudgetBytes, settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes)),
            Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetPercent), 0, 90));
    }, log);

    public static string DefaultDirectory()
    {
        try { return Path.Combine(FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder, "audio"); }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache", "audio"); }
    }

    /// <summary>The picker stores a parent directory. Wavee owns only this dedicated child beneath it.</summary>
    public static string ResolveDirectory(string? selectedBasePath) => string.IsNullOrWhiteSpace(selectedBasePath)
        ? Path.GetFullPath(DefaultDirectory())
        : Path.Combine(Path.GetFullPath(selectedBasePath), "WaveeAudioCache");

    public string CurrentDirectory => EnsureActiveDirectory(CurrentPolicy().Directory);

    Policy CurrentPolicy() => _policyProvider?.Invoke()
        ?? new Policy(true, _staticDirectory, AudioCacheBudgetMode.FixedBytes, Volatile.Read(ref _staticBudget), 0);

    // Activation is CHEAP by contract: create-directory + marker only. The reconcile/measure sweep that used to run here
    // (five recursive enumerations, an open+SHA per .map) sat synchronously inside the AudioPlaybackStack ctor — i.e. on
    // the login splash's "Starting audio" step — and cost 16–31 s against a cold-NTFS 3.5k-file cache (golive.audio_ms).
    // It is owed instead (armed via _scanPending) and paid off-path by WarmScan, the same treatment as the WASAPI
    // mix-format probe (FluentMediaAudioHost.Backend). Until the scan lands, _approxBytes undercounts: reads never
    // consult it, and the write-side budget check self-corrects on the next TrimInternal (which re-measures from disk).
    string EnsureActiveDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (_stateGate)
        {
            if (string.Equals(_activeDirectory, directory, StringComparison.OrdinalIgnoreCase)) return directory;
            _activeDirectory = directory;
            _scanGen++;
            _scanPending = true;
            _approxBytes = 0;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".wavee-audio-cache"), "Wavee encrypted audio cache v2");
            }
            catch { }
            return directory;
        }
    }

    /// <summary>Run the owed reconcile + measure sweep for the active root, if any. Idempotent per directory switch;
    /// runs on the CALLER's thread (callers background it — AudioPlaybackStack.StartProvisioning schedules it alongside
    /// the WASAPI backend warm). This is where torn .tmp files, invalid maps and orphan .enc bodies from crashed sessions
    /// get cleaned, and where the budget byte-count is seeded. Never throws.</summary>
    public void WarmScan()
    {
        string root;
        int gen;
        lock (_stateGate)
        {
            if (!_scanPending) return;
            _scanPending = false;
            root = _activeDirectory;
            gen = _scanGen;
        }
        long start = Environment.TickCount64;
        long bytes = 0;
        int files = 0, dropped = 0;
        try { (bytes, files, dropped) = ReconcileAndMeasure(root); }
        catch { }
        lock (_stateGate)
        {
            if (gen == _scanGen) Interlocked.Exchange(ref _approxBytes, bytes);
        }
        _log.Event(WaveeLogLevel.Info, "audio.cache.scan", "Audio body cache reconciled + measured",
            elapsedMs: Environment.TickCount64 - start,
            fields:
            [
                WaveeLogField.Of("files", files),
                WaveeLogField.Of("bytes", bytes),
                WaveeLogField.Of("dropped", dropped),
            ]);
    }

    /// <summary>The old ctor-time Reconcile + Measure folded into ONE enumeration pass (they were five). Deletions match
    /// the old semantics with one addition: .tmp files belonging to THIS process, or younger than 10 minutes, are live
    /// EnsureMap intermediates now that the scan runs concurrently with traffic — they are skipped, not reaped.</summary>
    (long Bytes, int Files, int Dropped) ReconcileAndMeasure(string root)
    {
        if (!Directory.Exists(root)) return (0, 0, 0);
        long bytes = 0;
        int files = 0, dropped = 0;
        string pidMarker = "." + Environment.ProcessId + ".";
        var maps = new List<string>();
        var encs = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Contains(pidMarker, StringComparison.Ordinal)) continue;
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(10)) continue; } catch { }
                TryDelete(file);
                dropped++;
            }
            else if (file.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) maps.Add(file);
            else if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)) encs.Add(file);
            else bytes += SafeLength(file);   // the .wavee-audio-cache marker
        }
        var validStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string map in maps)
        {
            if (!ValidMapFile(map))
            {
                TryDelete(map);
                TryDelete(Path.ChangeExtension(map, ".enc"));
                dropped++;
                continue;
            }
            validStems.Add(Path.ChangeExtension(map, null));
            bytes += SafeLength(map);
            files++;
        }
        foreach (string enc in encs)
        {
            if (!validStems.Contains(Path.ChangeExtension(enc, null))) { TryDelete(enc); dropped++; continue; }
            bytes += SafeLength(enc);
            files++;
        }
        RemoveEmptyDirectories(root);
        return (bytes, files, dropped);
    }

    static string Stem(string fileId)
    {
        string id = (fileId ?? "").Trim().ToLowerInvariant();
        bool safe = id.Length is >= 8 and <= 128 && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        return safe ? id : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    static string EntryDirectory(string root, string fileId) => Path.Combine(root, Stem(fileId)[..2]);
    static string EncPath(string root, string fileId) => Path.Combine(EntryDirectory(root, fileId), Stem(fileId) + ".enc");
    static string MapPath(string root, string fileId) => Path.Combine(EntryDirectory(root, fileId), Stem(fileId) + ".map");

    static FileStream OpenShared(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    object FileLock(string root, string fileId) => _fileLocks.GetOrAdd(root + "\0" + Stem(fileId), static _ => new object());

    static IDisposable? TryAcquireRoot(string root, int timeoutMs = 250)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        var mutex = new Mutex(false, "Wavee.AudioCache." + key);
        try
        {
            try { if (!mutex.WaitOne(timeoutMs)) { mutex.Dispose(); return null; } }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); return null; }
    }

    sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); }
    }

    public long? KnownSize(string fileId)
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root, 25);
            if (lease is null) return null;
            lock (FileLock(root, fileId)) return ReadHeader(root, fileId)?.TotalSize;
        }
        catch { return null; }
    }

    public void SetSize(string fileId, long size)
    {
        if (size <= 0 || size > int.MaxValue) return;
        var policy = CurrentPolicy();
        if (!policy.WriteEnabled) return;
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root);
            if (lease is null || !CurrentPolicy().WriteEnabled) return;
            lock (FileLock(root, fileId)) EnsureMap(root, fileId, size);
        }
        catch { }
    }

    public bool TryReadChunk(string fileId, int chunkIndex, Span<byte> destination, out int length)
    {
        length = 0;
        if (chunkIndex < 0) return false;
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root, 25);
            if (lease is null) return false;
            lock (FileLock(root, fileId))
            {
                var header = ReadHeader(root, fileId);
                if (header is null || chunkIndex >= header.Value.ChunkCount) return false;
                int expected = ExpectedLength(header.Value.TotalSize, chunkIndex);
                if (destination.Length < expected || !TryReadEntry(root, fileId, chunkIndex, out var digest)) return false;

                string enc = EncPath(root, fileId);
                if (!File.Exists(enc)) { ClearEntry(root, fileId, chunkIndex); return false; }
                using var fs = OpenShared(enc, FileMode.Open);
                long offset = (long)chunkIndex * ChunkBytes;
                if (fs.Length < offset + expected) { ClearEntry(root, fileId, chunkIndex); return false; }
                fs.Position = offset;
                fs.ReadExactly(destination[..expected]);
                Span<byte> actual = stackalloc byte[32];
                SHA256.HashData(destination[..expected], actual);
                if (!CryptographicOperations.FixedTimeEquals(actual, digest))
                {
                    ClearEntry(root, fileId, chunkIndex);
                    return false;
                }
                length = expected;
                TouchMap(root, fileId);
                return true;
            }
        }
        catch { return false; }
    }

    public void WriteChunk(string fileId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        if (chunkIndex < 0 || data.IsEmpty) return;
        var policy = CurrentPolicy();
        if (!policy.WriteEnabled) return;
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root);
            if (lease is null || !CurrentPolicy().WriteEnabled) return;
            lock (FileLock(root, fileId))
            {
                var header = ReadHeader(root, fileId);
                if (header is null || chunkIndex >= header.Value.ChunkCount) return;
                int expected = ExpectedLength(header.Value.TotalSize, chunkIndex);
                if (data.Length != expected) return;
                long growth = ProjectedGrowth(root, fileId, chunkIndex, expected);
                if (!CanCommit(policy, root, growth)) return;

                string enc = EncPath(root, fileId);
                Directory.CreateDirectory(Path.GetDirectoryName(enc)!);
                using (var fs = OpenShared(enc, FileMode.OpenOrCreate))
                {
                    long offset = (long)chunkIndex * ChunkBytes;
                    fs.Position = offset;
                    fs.Write(data);
                    fs.Flush(true);
                }

                Span<byte> digest = stackalloc byte[32];
                SHA256.HashData(data, digest);
                CommitEntry(root, fileId, chunkIndex, digest);
                Interlocked.Add(ref _approxBytes, growth);
                TouchMap(root, fileId, force: true);
            }
        }
        catch { }
    }

    static int ExpectedLength(long totalSize, int chunkIndex)
    {
        long offset = (long)chunkIndex * ChunkBytes;
        return (int)Math.Min(ChunkBytes, Math.Max(0, totalSize - offset));
    }

    long ProjectedGrowth(string root, string fileId, int chunkIndex, int expected)
    {
        string enc = EncPath(root, fileId);
        long old = 0;
        try { if (File.Exists(enc)) old = new FileInfo(enc).Length; } catch { }
        long next = Math.Max(old, (long)chunkIndex * ChunkBytes + expected);
        return Math.Max(0, next - old);
    }

    bool CanCommit(Policy policy, string root, long growth)
    {
        var capacity = Capacity(root, policy);
        if (!capacity.Available || capacity.FreeBytes - growth < capacity.ReserveBytes) return false;
        if (capacity.BudgetBytes is not { } budget) return true;
        if (Volatile.Read(ref _approxBytes) + growth <= budget) return true;
        TrimInternal(root, budget);
        return Volatile.Read(ref _approxBytes) + growth <= budget;
    }

    public AudioBodyCacheStatus Status()
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        var cap = Capacity(root, policy);
        return new AudioBodyCacheStatus(root, cap.Available, DirectoryBytes(), cap.BudgetBytes,
            cap.FreeBytes, cap.ReserveBytes, policy.WriteEnabled);
    }

    readonly record struct CapacityState(bool Available, long FreeBytes, long ReserveBytes, long? BudgetBytes);

    static CapacityState Capacity(string root, Policy policy)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(volumeRoot)) return new(false, 0, MinimumReserveBytes, null);
            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady) return new(false, 0, MinimumReserveBytes, null);
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            long reserve = Math.Max(MinimumReserveBytes, total / 20);
            long? budget = policy.Mode switch
            {
                AudioCacheBudgetMode.Unlimited => null,
                AudioCacheBudgetMode.FixedBytes => Math.Max(MinBudgetBytes, policy.FixedBytes),
                _ when policy.Percent == 0 => Math.Clamp(total / 10, 16L << 30, 128L << 30),
                _ => Math.Max(MinBudgetBytes, (long)(total * (policy.Percent / 100d))),
            };
            return new(true, free, reserve, budget);
        }
        catch { return new(false, 0, MinimumReserveBytes, null); }
    }

    void EnsureMap(string root, string fileId, long totalSize)
    {
        var current = ReadHeader(root, fileId);
        if (current?.TotalSize == totalSize) return;
        if (current is not null) DeletePair(root, fileId);
        int chunks = checked((int)((totalSize + ChunkBytes - 1) / ChunkBytes));
        string map = MapPath(root, fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        string tmp = map + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), ChunkBytes);
        BitConverter.TryWriteBytes(header.AsSpan(8, 8), totalSize);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), chunks);
        SHA256.HashData(header.AsSpan(0, HeaderCoreBytes), header.AsSpan(HeaderCoreBytes, 32));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(header);
                fs.SetLength(HeaderBytes + (long)chunks * EntryBytes);
                fs.Flush(true);
            }
            File.Move(tmp, map, true);
            Interlocked.Add(ref _approxBytes, new FileInfo(map).Length);
        }
        finally { TryDelete(tmp); }
    }

    MapHeader? ReadHeader(string root, string fileId)
    {
        string map = MapPath(root, fileId);
        if (!File.Exists(map)) return null;
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            using var fs = OpenShared(map, FileMode.Open);
            if (fs.Length < HeaderBytes) { DeletePair(root, fileId); return null; }
            fs.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic)) { DeletePair(root, fileId); return null; }
            int chunkBytes = BitConverter.ToInt32(header.Slice(4, 4));
            long total = BitConverter.ToInt64(header.Slice(8, 8));
            int chunks = BitConverter.ToInt32(header.Slice(16, 4));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(header[..HeaderCoreBytes], digest);
            long expectedFile = HeaderBytes + (long)chunks * EntryBytes;
            if (chunkBytes != ChunkBytes || total <= 0 || total > int.MaxValue || chunks <= 0 ||
                chunks != (total + ChunkBytes - 1) / ChunkBytes || fs.Length != expectedFile ||
                !CryptographicOperations.FixedTimeEquals(digest, header.Slice(HeaderCoreBytes, 32)))
            {
                DeletePair(root, fileId);
                return null;
            }
            return new(total, chunks);
        }
        catch { return null; }
    }

    bool TryReadEntry(string root, string fileId, int index, out byte[] digest)
    {
        digest = Array.Empty<byte>();
        string map = MapPath(root, fileId);
        using var fs = OpenShared(map, FileMode.Open);
        fs.Position = HeaderBytes + (long)index * EntryBytes;
        Span<byte> entry = stackalloc byte[EntryBytes];
        fs.ReadExactly(entry);
        if (entry[0] != 1) return false;
        digest = entry[1..].ToArray();
        return true;
    }

    void CommitEntry(string root, string fileId, int index, ReadOnlySpan<byte> digest)
    {
        string map = MapPath(root, fileId);
        using var fs = OpenShared(map, FileMode.Open);
        fs.Position = HeaderBytes + (long)index * EntryBytes;
        Span<byte> entry = stackalloc byte[EntryBytes];
        entry[0] = 1;
        digest.CopyTo(entry[1..]);
        fs.Write(entry);
        fs.Flush(true);
    }

    void ClearEntry(string root, string fileId, int index)
    {
        try
        {
            string map = MapPath(root, fileId);
            using var fs = OpenShared(map, FileMode.Open);
            fs.Position = HeaderBytes + (long)index * EntryBytes;
            fs.WriteByte(0);
            fs.Flush(true);
        }
        catch { }
    }

    public void Invalidate(string fileId)
    {
        string root = EnsureActiveDirectory(CurrentPolicy().Directory);
        try { using var lease = TryAcquireRoot(root); if (lease is not null) lock (FileLock(root, fileId)) DeletePair(root, fileId); }
        catch { }
    }

    void DeletePair(string root, string fileId)
    {
        TryDelete(EncPath(root, fileId));
        TryDelete(MapPath(root, fileId));
    }

    public void ClearAll()
    {
        string root = EnsureActiveDirectory(CurrentPolicy().Directory);
        try
        {
            using var lease = TryAcquireRoot(root, 2000);
            if (lease is null || !IsOwnedRoot(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            RemoveEmptyDirectories(root);
            Interlocked.Exchange(ref _approxBytes, Measure(root));
        }
        catch { }
    }

    public long DirectoryBytes() => Measure(EnsureActiveDirectory(CurrentPolicy().Directory));

    public void SetBudget(long budgetBytes)
    {
        if (budgetBytes <= 0) return;
        Interlocked.Exchange(ref _staticBudget, Math.Max(MinBudgetBytes, budgetBytes));
        TrimToBudget(budgetBytes);
    }

    public long TrimToBudget(long budgetBytes) => TrimInternal(EnsureActiveDirectory(CurrentPolicy().Directory), Math.Max(MinBudgetBytes, budgetBytes));

    public long Trim()
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        var budget = Capacity(root, policy).BudgetBytes;
        return budget is null ? 0 : TrimInternal(root, budget.Value);
    }

    long TrimInternal(string root, long budget)
    {
        if (!Monitor.TryEnter(_trimLock)) return 0;
        try
        {
            using var lease = TryAcquireRoot(root, 2000);
            if (lease is null) return 0;
            var maps = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories).Select(static p => new FileInfo(p)).ToList()
                : [];
            long total = Measure(root);
            if (total <= budget) { Interlocked.Exchange(ref _approxBytes, total); return 0; }
            maps.Sort(static (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            long target = (long)(budget * 0.9);
            long freed = 0;
            foreach (var map in maps)
            {
                if (total <= target) break;
                string enc = Path.ChangeExtension(map.FullName, ".enc");
                long bytes = map.Length + SafeLength(enc);
                TryDelete(enc);
                TryDelete(map.FullName);
                total -= bytes;
                freed += bytes;
            }
            RemoveEmptyDirectories(root);
            Interlocked.Exchange(ref _approxBytes, Math.Max(0, total));
            return freed;
        }
        catch { return 0; }
        finally { Monitor.Exit(_trimLock); }
    }

    /// <summary>Prepares a new owned root. The caller persists the new base path only after this succeeds.</summary>
    public Task<bool> PrepareRelocationAsync(string newBasePath, AudioCacheRelocationMode mode, CancellationToken ct = default)
        => Task.Run(() => PrepareRelocation(newBasePath, mode, ct), ct);

    bool PrepareRelocation(string newBasePath, AudioCacheRelocationMode mode, CancellationToken ct)
    {
        string oldRoot = EnsureActiveDirectory(CurrentPolicy().Directory);
        string newRoot = ResolveDirectory(newBasePath);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            Directory.CreateDirectory(newRoot);
            File.WriteAllText(Path.Combine(newRoot, ".wavee-audio-cache"), "Wavee encrypted audio cache v2");
            using var oldLease = TryAcquireRoot(oldRoot, 5000);
            using var newLease = TryAcquireRoot(newRoot, 5000);
            if (oldLease is null || newLease is null) return false;
            if (mode == AudioCacheRelocationMode.Move)
            {
                foreach (string map in Directory.EnumerateFiles(oldRoot, "*.map", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    CopyValidatedPair(oldRoot, newRoot, Path.GetFileNameWithoutExtension(map), ct);
                }
                DeleteOwnedContents(oldRoot);
            }
            else
            {
                DeleteOwnedContents(newRoot);
                File.WriteAllText(Path.Combine(newRoot, ".wavee-audio-cache"), "Wavee encrypted audio cache v2");
                if (mode == AudioCacheRelocationMode.StartEmptyDeleteOld) DeleteOwnedContents(oldRoot);
            }
            return true;
        }
        catch { return false; }
    }

    void CopyValidatedPair(string oldRoot, string newRoot, string stem, CancellationToken ct)
    {
        string oldMap = Directory.EnumerateFiles(oldRoot, stem + ".map", SearchOption.AllDirectories).FirstOrDefault() ?? "";
        if (oldMap.Length == 0) return;
        string oldEnc = Path.ChangeExtension(oldMap, ".enc");
        if (!File.Exists(oldEnc)) return;
        string shard = stem[..2];
        string newDir = Path.Combine(newRoot, shard);
        Directory.CreateDirectory(newDir);
        string newMap = Path.Combine(newDir, stem + ".map");
        string newEnc = Path.Combine(newDir, stem + ".enc");
        using var srcMap = OpenShared(oldMap, FileMode.Open);
        Span<byte> headerBytes = stackalloc byte[HeaderBytes];
        srcMap.ReadExactly(headerBytes);
        if (!headerBytes[..4].SequenceEqual(Magic)) return;
        long total = BitConverter.ToInt64(headerBytes.Slice(8, 8));
        int chunks = BitConverter.ToInt32(headerBytes.Slice(16, 4));
        using var dstMap = new FileStream(newMap, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        dstMap.Write(headerBytes);
        dstMap.SetLength(HeaderBytes + (long)chunks * EntryBytes);
        using var srcEnc = OpenShared(oldEnc, FileMode.Open);
        using var dstEnc = OpenShared(newEnc, FileMode.OpenOrCreate);
        byte[] buffer = new byte[ChunkBytes];
        byte[] entryBytes = new byte[EntryBytes];
        byte[] digestBytes = new byte[32];
        for (int i = 0; i < chunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            srcMap.Position = HeaderBytes + (long)i * EntryBytes;
            Span<byte> entry = entryBytes;
            entry.Clear();
            srcMap.ReadExactly(entry);
            if (entry[0] != 1) continue;
            int len = ExpectedLength(total, i);
            srcEnc.Position = (long)i * ChunkBytes;
            srcEnc.ReadExactly(buffer.AsSpan(0, len));
            Span<byte> digest = digestBytes;
            SHA256.HashData(buffer.AsSpan(0, len), digest);
            if (!CryptographicOperations.FixedTimeEquals(digest, entry[1..])) continue;
            dstEnc.Position = (long)i * ChunkBytes;
            dstEnc.Write(buffer, 0, len);
            dstMap.Position = HeaderBytes + (long)i * EntryBytes;
            dstMap.Write(entry);
        }
        dstEnc.Flush(true);
        dstMap.Flush(true);
    }

    static bool ValidMapFile(string map)
    {
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            using var fs = OpenShared(map, FileMode.Open);
            if (fs.Length < HeaderBytes) return false;
            fs.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic)) return false;
            int chunkBytes = BitConverter.ToInt32(header.Slice(4, 4));
            long total = BitConverter.ToInt64(header.Slice(8, 8));
            int chunks = BitConverter.ToInt32(header.Slice(16, 4));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(header[..HeaderCoreBytes], digest);
            return chunkBytes == ChunkBytes && total > 0 && total <= int.MaxValue && chunks > 0 &&
                   chunks == (total + ChunkBytes - 1) / ChunkBytes && fs.Length == HeaderBytes + (long)chunks * EntryBytes &&
                   CryptographicOperations.FixedTimeEquals(digest, header.Slice(HeaderCoreBytes, 32));
        }
        catch { return false; }
    }

    void TouchMap(string root, string fileId, bool force = false)
    {
        string map = MapPath(root, fileId);
        long now = DateTime.UtcNow.Ticks;
        string key = root + "\0" + Stem(fileId);
        long prior = _lastTouch.GetOrAdd(key, 0);
        if (!force && now - prior < TimeSpan.FromHours(1).Ticks) return;
        if (!_lastTouch.TryUpdate(key, now, prior) && prior != 0) return;
        try { File.SetLastAccessTimeUtc(map, new DateTime(now, DateTimeKind.Utc)); } catch { }
    }

    static bool IsOwnedRoot(string root) => File.Exists(Path.Combine(root, ".wavee-audio-cache")) ||
                                             string.Equals(Path.GetFullPath(root), Path.GetFullPath(DefaultDirectory()), StringComparison.OrdinalIgnoreCase);

    static void DeleteOwnedContents(string root)
    {
        if (!IsOwnedRoot(root) || !Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(file);
        RemoveEmptyDirectories(root);
    }

    static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(static d => d.Length))
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    static long Measure(string root)
    {
        long total = 0;
        try { if (Directory.Exists(root)) foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) total += SafeLength(file); }
        catch { }
        return total;
    }

    static long SafeLength(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; } }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
