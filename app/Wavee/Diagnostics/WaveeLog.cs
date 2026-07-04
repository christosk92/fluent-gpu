using System.IO;

namespace Wavee;

/// <summary>
/// The always-on app logger: a lock-guarded ring buffer (last N entries — feeds the in-app Diagnostics page) plus a
/// rolling crash-log file for Error+. Logging never throws. AOT-clean (no reflection, no JSON serializer needed for the
/// text sink). Singleton via <see cref="Instance"/>; configured once from <c>Program.Main</c>.
/// </summary>
public sealed class WaveeLog : IWaveeLog
{
    public static WaveeLog Instance { get; } = new();

    readonly object _gate = new();
    readonly WaveeLogEntry[] _ring = new WaveeLogEntry[512];
    int _head, _count;
    string? _crashLogPath;
    Action<string>? _echo;

    public WaveeLogLevel MinLevel { get; set; } = WaveeLogLevel.Debug;

    /// <summary>Set the crash-log path (Error+ appended) and an optional echo sink (e.g. Console.Error in dev).</summary>
    public void Configure(string? crashLogPath = null, Action<string>? echo = null)
    {
        _crashLogPath = crashLogPath;
        _echo = echo;
        if (crashLogPath is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!); } catch { /* best effort */ }
        }
    }

    public void Log(WaveeLogLevel level, string category, string message, Exception? ex = null)
    {
        if (level < MinLevel) return;
        var entry = new WaveeLogEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), level, category, message, ex?.ToString());
        lock (_gate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
        string line = entry.Format();
        _echo?.Invoke(line);
        // Persist Error+ (the crash trail) AND the full "audio" pipeline trace (Info+) — the audio path is otherwise
        // invisible in a windowed/AOT build (no console), so its staged trace goes to the file to be tailable/diagnosable.
        if ((level >= WaveeLogLevel.Error || category == "audio") && _crashLogPath is { } path) TryAppend(path, entry, line);
    }

    /// <summary>Snapshot of recent entries, oldest → newest (for the Diagnostics page).</summary>
    public WaveeLogEntry[] Snapshot()
    {
        lock (_gate)
        {
            var outp = new WaveeLogEntry[_count];
            int start = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < _count; i++) outp[i] = _ring[(start + i) % _ring.Length];
            return outp;
        }
    }

    /// <summary>An <see cref="Action{String}"/> to plug into the engine's <c>Diag.Sink</c> (dev only): engine
    /// diagnostics flow into the same app stream.</summary>
    public static Action<string> DiagSink => static s => Instance.Log(WaveeLogLevel.Debug, "engine", s);

    static void TryAppend(string path, WaveeLogEntry e, string line)
    {
        try { File.AppendAllText(path, $"{DateTimeOffset.FromUnixTimeMilliseconds(e.UnixMs):u} {line}{Environment.NewLine}"); }
        catch { /* logging must never throw */ }
    }
}
