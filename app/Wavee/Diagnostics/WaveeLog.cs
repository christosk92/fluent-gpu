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

    public static WaveeLog Instance { get; } = new();

    readonly object _ringGate = new();
    readonly WaveeLogEntry[] _ring;
    int _head, _count;
    long _nextSequence, _version;

    readonly object _fileGate = new();
    readonly Queue<string> _fileQueue = new();
    bool _drainScheduled;
    int _droppedFileLines;

    string? _filePath;
    Action<string>? _echo;
    long _maxFileBytes = DefaultMaxFileBytes;
    int _retainedFiles = DefaultRetainedFiles;

    public WaveeLogLevel MinLevel { get; set; } = WaveeLogLevel.Info;
    public WaveeLogLevel FileMinLevel { get; set; } = WaveeLogLevel.Info;
    public string? FilePath => _filePath;
    public long Version { get { lock (_ringGate) return _version; } }

    public WaveeLog(int ringCapacity = DefaultRingCapacity)
    {
        _ring = new WaveeLogEntry[Math.Max(1, ringCapacity)];
    }

    /// <summary>Set the file path and optional dev echo sink. The legacy name is retained for existing callers.</summary>
    public void Configure(string? crashLogPath = null, Action<string>? echo = null,
        WaveeLogLevel? minLevel = null, WaveeLogLevel? fileMinLevel = null, long? maxFileBytes = null, int? retainedFiles = null)
    {
        _filePath = crashLogPath;
        _echo = echo;
        MinLevel = minLevel ?? ResolveLevel(Environment.GetEnvironmentVariable("WAVEE_LOG_LEVEL"), WaveeLogLevel.Info);
        FileMinLevel = fileMinLevel ?? ResolveLevel(Environment.GetEnvironmentVariable("WAVEE_LOG_FILE_LEVEL"), WaveeLogLevel.Info);
        _maxFileBytes = maxFileBytes.GetValueOrDefault(DefaultMaxFileBytes);
        _retainedFiles = Math.Max(1, retainedFiles.GetValueOrDefault(DefaultRetainedFiles));

        if (crashLogPath is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!); } catch { }
        }
    }

    public void Log(WaveeLogLevel level, string category, string message, Exception? ex = null)
    {
        if (category == "audio" && message.StartsWith("playback failed [", StringComparison.Ordinal))
            return;
        Write(level, category, "", message, null, -1, null, ex);
    }

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
        lock (_ringGate)
        {
            var outp = new WaveeLogEntry[_count];
            int start = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < _count; i++) outp[i] = _ring[(start + i) % _ring.Length];
            return outp;
        }
    }

    /// <summary>Test hook: synchronously drains any queued file lines.</summary>
    public void FlushForTests()
    {
        while (true)
        {
            string[] batch;
            lock (_fileGate)
            {
                if (_fileQueue.Count == 0) { _drainScheduled = false; return; }
                batch = DequeueBatchLocked();
            }
            WriteBatch(batch);
        }
    }

    /// <summary>Best-effort synchronous drain of queued file lines. Safe to call from crash paths.</summary>
    public void Flush()
    {
        try { FlushForTests(); } catch { }
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

        lock (_ringGate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            _version++;
        }

        string line = entry.Format();
        try { _echo?.Invoke(line); } catch { }

        if (level >= FileMinLevel && _filePath is not null)
            EnqueueFileLine(FormatFileLine(entry, line));
    }

    static WaveeLogField[] CopyFields(WaveeLogField[] fields)
    {
        int n = Math.Min(MaxFieldCount, fields.Length);
        var copy = new WaveeLogField[n];
        for (int i = 0; i < n; i++) copy[i] = fields[i];
        return copy;
    }

    // t= (unix ms) lets the diagnostics page rebuild timestamps + session boundaries when re-reading the file;
    // lines written by older builds simply lack it (the parser treats them as timestamp-less).
    static string FormatFileLine(WaveeLogEntry e, string formatted)
        => "seq=" + e.Sequence.ToString(CultureInfo.InvariantCulture)
            + " tid=" + e.ThreadId.ToString(CultureInfo.InvariantCulture)
            + " t=" + e.UnixMs.ToString(CultureInfo.InvariantCulture)
            + " " + formatted;

    void EnqueueFileLine(string line)
    {
        lock (_fileGate)
        {
            if (_fileQueue.Count >= MaxQueueLines)
            {
                _fileQueue.Dequeue();
                _droppedFileLines++;
            }
            _fileQueue.Enqueue(line);
            if (!_drainScheduled)
            {
                _drainScheduled = true;
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.DrainFileQueue(), this, preferLocal: false);
            }
        }
    }

    void DrainFileQueue()
    {
        while (true)
        {
            string[] batch;
            lock (_fileGate)
            {
                if (_fileQueue.Count == 0) { _drainScheduled = false; return; }
                batch = DequeueBatchLocked();
            }
            WriteBatch(batch);
        }
    }

    string[] DequeueBatchLocked()
    {
        int extra = _droppedFileLines > 0 ? 1 : 0;
        int n = Math.Min(512, _fileQueue.Count);
        var batch = new string[n + extra];
        int at = 0;
        if (_droppedFileLines > 0)
        {
            batch[at++] = "W [log] file sink dropped queued lines count="
                + _droppedFileLines.ToString(CultureInfo.InvariantCulture);
            _droppedFileLines = 0;
        }
        for (int i = 0; i < n; i++) batch[at++] = _fileQueue.Dequeue();
        return batch;
    }

    void WriteBatch(string[] lines)
    {
        var path = _filePath;
        if (path is null || lines.Length == 0) return;

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            RollIfNeeded(path, dir);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, new UTF8Encoding(false));
            for (int i = 0; i < lines.Length; i++) sw.WriteLine(lines[i]);
        }
        catch { }
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

    static WaveeLogLevel ResolveLevel(string? raw, WaveeLogLevel fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return Enum.TryParse<WaveeLogLevel>(raw.Trim(), ignoreCase: true, out var level) ? level : fallback;
    }
}
