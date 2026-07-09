using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Wavee;

/// <summary>
/// Always-on app logger: structured ring buffer for the Settings diagnostics page plus an Info+ local rolling file.
/// Logging never throws and does not depend on reflection, JSON serialization, or third-party sinks.
/// </summary>
public sealed class WaveeLog : IWaveeLog
{
    const int DefaultRingCapacity = 4096;
    const int MaxFieldCount = 16;
    const int MaxQueueLines = 8192;
    const long DefaultMaxFileBytes = 10L * 1024L * 1024L;
    const int DefaultRetainedFiles = 7;

    /// <summary>8 lowercase hex chars identifying THIS process run — stamped on every file line (sid=) so sessions
    /// re-read from disk can be split by run without the fragile "Wavee starting" heuristic. Cached pid alongside.</summary>
    public static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
    static readonly int ProcessId = Environment.ProcessId;

    public static WaveeLog Instance { get; } = new();

    readonly object _ringGate = new();
    WaveeLogEntry[] _ring;
    int _head, _count;
    long _nextSequence, _version;

    readonly object _fileGate = new();
    readonly object _writeGate = new();
    readonly Queue<WaveeLogEntry> _fileQueue = new();
    bool _drainScheduled;
    int _droppedFileLines;

    // Persistent sink handle — touched only under _writeGate (drain thread + FlushForTests serialize on it).
    FileStream? _fs;
    StreamWriter? _sw;
    bool _fileSinkFailed;

    string? _filePath;
    Action<string>? _echo;
    long _maxFileBytes = DefaultMaxFileBytes;
    int _retainedFiles = DefaultRetainedFiles;

    /// <summary>Test-only: drain the file queue synchronously on enqueue (eliminates ThreadPool races in unit tests).</summary>
    internal bool SyncFileDrainForTests { get; set; }

    public WaveeLogLevel MinLevel { get; set; } = WaveeLogLevel.Info;
    /// <summary>Upward-only file filter: a line reaches the file when its level is >= both MinLevel (the master gate)
    /// and FileMinLevel. Lowering it below MinLevel has no effect — MinLevel already dropped the entry.</summary>
    public WaveeLogLevel FileMinLevel { get; set; } = WaveeLogLevel.Info;
    public string? FilePath => _filePath;
    public long Version { get { lock (_ringGate) return _version; } }

    /// <summary>True when the corresponding env override is set — the runtime level UI disables its box then.</summary>
    public static bool EnvMinLevelSet => EnvLevel("WAVEE_LOG_LEVEL") is not null;
    public static bool EnvFileMinLevelSet => EnvLevel("WAVEE_LOG_FILE_LEVEL") is not null;

    public WaveeLog(int ringCapacity = DefaultRingCapacity)
    {
        _ring = new WaveeLogEntry[Math.Max(1, ringCapacity)];
    }

    public bool IsEnabled(WaveeLogLevel level) => level >= MinLevel;

    /// <summary>Set the file path and optional dev echo sink. The legacy name is retained for existing callers.
    /// Level precedence is env &gt; explicit arg (settings) &gt; default — the env vars now always win.</summary>
    public void Configure(string? crashLogPath = null, Action<string>? echo = null,
        WaveeLogLevel? minLevel = null, WaveeLogLevel? fileMinLevel = null, long? maxFileBytes = null,
        int? retainedFiles = null, int? ringCapacity = null)
    {
        if (!string.Equals(_filePath, crashLogPath, StringComparison.OrdinalIgnoreCase))
        {
            lock (_writeGate) CloseStream();   // path change → reopen on next batch
        }
        _filePath = crashLogPath;
        _echo = echo;
        MinLevel = EnvLevel("WAVEE_LOG_LEVEL") ?? minLevel ?? WaveeLogLevel.Info;
        FileMinLevel = EnvLevel("WAVEE_LOG_FILE_LEVEL") ?? fileMinLevel ?? WaveeLogLevel.Info;
        _maxFileBytes = maxFileBytes.GetValueOrDefault(DefaultMaxFileBytes);
        _retainedFiles = Math.Max(1, retainedFiles.GetValueOrDefault(DefaultRetainedFiles));

        int? ringReq = EnvInt("WAVEE_LOG_RING") ?? ringCapacity;
        if (ringReq is int rc) ReallocRing(rc);

        if (crashLogPath is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!); } catch { }
            // Parent-side retention: keep the child-process logs bounded the same way the main file rolls. A child
            // (its own file is audio-child-*.log) must NOT sweep its siblings, so only the parent does this.
            if (!Path.GetFileName(crashLogPath).StartsWith("audio-child-", StringComparison.Ordinal))
                SweepAudioChildLogs(Path.GetDirectoryName(crashLogPath));
        }
    }

    public void Log(WaveeLogLevel level, string category, string message, Exception? ex = null) =>
        Write(level, category, "", message, null, -1, null, ex);

    public void Event(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId = null, long elapsedMs = -1, Exception? ex = null, params WaveeLogField[] fields) =>
        Write(level, category, eventId, message, operationId, elapsedMs, fields, ex);

    public void ClearRing()
    {
        lock (_ringGate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _version++;
        }
    }

    /// <summary>Snapshot of recent entries, oldest to newest.</summary>
    public WaveeLogEntry[] Snapshot()
    {
        lock (_ringGate) return SnapshotLocked();
    }

    WaveeLogEntry[] SnapshotLocked()
    {
        var outp = new WaveeLogEntry[_count];
        int start = (_head - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++) outp[i] = _ring[(start + i) % _ring.Length];
        return outp;
    }

    /// <summary>Test hook: synchronously drains any queued file lines and releases the file handle so tests can
    /// read the log file (the production path keeps the stream open across batches).</summary>
    public void FlushForTests()
    {
        DrainFileQueue();
        lock (_writeGate) CloseStream();
    }

    /// <summary>Best-effort synchronous drain of queued file lines. Safe to call from crash paths.</summary>
    public void Flush()
    {
        try { DrainFileQueue(); } catch { }
    }

    /// <summary>An action to plug into the engine Diag sink. Engine noise remains Debug-gated by MinLevel.</summary>
    public static Action<string> DiagSink => static s => Instance.Log(WaveeLogLevel.Debug, "engine", s);

    void Write(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId, long elapsedMs, WaveeLogField[]? fields, Exception? ex)
    {
        if (level < MinLevel) return;

        var safeFields = fields is { Length: > 0 } ? CopyFields(fields) : null;
        var entry = new WaveeLogEntry(
            Interlocked.Increment(ref _nextSequence),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            level,
            string.IsNullOrWhiteSpace(category) ? "app" : category,
            eventId ?? "",
            message ?? "",
            operationId,
            Environment.CurrentManagedThreadId,
            elapsedMs,
            safeFields,
            ex?.ToString());

        PushRing(entry);

        // Lazy: format only for the dev/probe echo (no consumer → no formatted string).
        if (_echo is { } e) { try { e(entry.Format()); } catch { } }

        if (level >= FileMinLevel && _filePath is not null)
            EnqueueFileEntry(entry);
    }

    void PushRing(WaveeLogEntry entry)
    {
        lock (_ringGate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            _version++;
        }
    }

    // A self-report entry ("log" category) pushed ring-only — never enqueued to the file (avoids failure recursion).
    void PushInternalRingOnly(WaveeLogLevel level, string message)
    {
        var entry = new WaveeLogEntry(
            Interlocked.Increment(ref _nextSequence), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            level, "log", "", message, null, Environment.CurrentManagedThreadId, -1, null, null);
        PushRing(entry);
        if (_echo is { } e) { try { e(entry.Format()); } catch { } }
    }

    static WaveeLogField[] CopyFields(WaveeLogField[] fields)
    {
        int n = Math.Min(MaxFieldCount, fields.Length);
        var copy = new WaveeLogField[n];
        for (int i = 0; i < n; i++) copy[i] = fields[i];
        return copy;
    }

    // File-line prefix, carried before the formatted body:
    //   seq=17 tid=4 t=1720512345678 sid=3f9c2a1b pid=31544 I [connect] session.start - started
    // t= (unix ms), sid= (per-run id) and pid= let the diagnostics page rebuild timestamps, session boundaries and the
    // owning process when re-reading; older builds omit these tokens and the parser treats them as absent.
    static string FormatFileLine(WaveeLogEntry e)
        => "seq=" + e.Sequence.ToString(CultureInfo.InvariantCulture)
            + " tid=" + e.ThreadId.ToString(CultureInfo.InvariantCulture)
            + " t=" + e.UnixMs.ToString(CultureInfo.InvariantCulture)
            + " sid=" + SessionId
            + " pid=" + ProcessId.ToString(CultureInfo.InvariantCulture)
            + " " + e.Format();

    void EnqueueFileEntry(WaveeLogEntry entry)
    {
        bool sync = SyncFileDrainForTests;
        lock (_fileGate)
        {
            if (_fileQueue.Count >= MaxQueueLines)
            {
                _fileQueue.Dequeue();
                _droppedFileLines++;
            }
            _fileQueue.Enqueue(entry);
            if (sync)
            {
                _drainScheduled = true;
            }
            else if (!_drainScheduled)
            {
                _drainScheduled = true;
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.DrainFileQueue(), this, preferLocal: false);
            }
        }
        if (sync) DrainFileQueue();
    }

    void DrainFileQueue()
    {
        while (true)
        {
            WaveeLogEntry[] batch;
            int dropped;
            lock (_fileGate)
            {
                if (_fileQueue.Count == 0) { _drainScheduled = false; return; }
                (batch, dropped) = DequeueBatchLocked();
            }
            WriteBatch(batch, dropped);
        }
    }

    (WaveeLogEntry[] batch, int dropped) DequeueBatchLocked()
    {
        int dropped = _droppedFileLines;
        _droppedFileLines = 0;
        int n = Math.Min(512, _fileQueue.Count);
        var batch = new WaveeLogEntry[n];
        for (int i = 0; i < n; i++) batch[i] = _fileQueue.Dequeue();
        return (batch, dropped);
    }

    void WriteBatch(WaveeLogEntry[] entries, int dropped)
    {
        var path = _filePath;
        if (path is null || (entries.Length == 0 && dropped == 0)) return;

        lock (_writeGate)
        {
            try
            {
                EnsureStream(path);
                if (dropped > 0)
                    _sw!.WriteLine("W [log] file sink dropped queued lines count="
                        + dropped.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < entries.Length; i++) _sw!.WriteLine(FormatFileLine(entries[i]));
                _sw!.Flush();
                if (_fileSinkFailed)
                {
                    _fileSinkFailed = false;
                    PushInternalRingOnly(WaveeLogLevel.Info, "file sink recovered path=" + path);
                }
                if (_fs is { } fs && fs.Length >= _maxFileBytes) CloseStream();   // roll on next EnsureStream
            }
            catch
            {
                CloseStream();   // reopen next batch → feeds the recovery signal
                if (!_fileSinkFailed)
                {
                    _fileSinkFailed = true;
                    PushInternalRingOnly(WaveeLogLevel.Warning, "file sink write failed - dropping lines path=" + path);
                }
                // The lines in this batch are dropped; the next successful batch reports recovery.
            }
        }
    }

    void EnsureStream(string path)
    {
        if (_sw is not null) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        RollIfNeeded(path, dir);
        _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _sw = new StreamWriter(_fs, new UTF8Encoding(false));
    }

    void CloseStream()
    {
        try { _sw?.Flush(); } catch { }
        try { _sw?.Dispose(); } catch { }   // disposes the underlying FileStream
        _sw = null;
        _fs = null;
    }

    void RollIfNeeded(string path, string? dir)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileBytes) return;
            string root = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string rolled = Path.Combine(dir ?? "", root + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ext);
            File.Move(path, rolled, overwrite: true);
            PruneRolledFiles(dir, root, ext);
        }
        catch { }
    }

    void PruneRolledFiles(string? dir, string root, string ext)
    {
        if (dir is null) return;
        try
        {
            var files = Directory.GetFiles(dir, root + "-*" + ext);
            Array.Sort(files, static (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            for (int i = _retainedFiles; i < files.Length; i++)
                try { File.Delete(files[i]); } catch { }
        }
        catch { }
    }

    void SweepAudioChildLogs(string? dir)
    {
        if (dir is null) return;
        try
        {
            var files = Directory.GetFiles(dir, "audio-child-*.log");
            Array.Sort(files, static (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            for (int i = _retainedFiles; i < files.Length; i++)
                try { File.Delete(files[i]); } catch { }
        }
        catch { }
    }

    void ReallocRing(int capacity)
    {
        capacity = Math.Max(1, capacity);
        lock (_ringGate)
        {
            if (capacity == _ring.Length) return;
            var snapshot = SnapshotLocked();               // oldest → newest
            var newRing = new WaveeLogEntry[capacity];
            int keep = Math.Min(snapshot.Length, capacity);
            int from = snapshot.Length - keep;             // preserve the newest `keep`
            for (int i = 0; i < keep; i++) newRing[i] = snapshot[from + i];
            _ring = newRing;
            _head = keep % capacity;
            _count = keep;
            _version++;
        }
    }

    static WaveeLogLevel? EnvLevel(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<WaveeLogLevel>(raw.Trim(), ignoreCase: true, out var level) ? level : null;
    }

    static int? EnvInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : null;
    }
}
