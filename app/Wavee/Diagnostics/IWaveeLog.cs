namespace Wavee;

// Production observability, the app-side seam. AOT-safe (no reflection, no Newtonsoft). The engine's `Diag` is
// debug-gated (compiled out in Release); THIS log is always-on, structured, ring-buffered, and surfaced in the
// in-app Diagnostics page. Wire `Diag.Sink = WaveeLog.DiagSink` in dev to fold engine diagnostics into the same stream.

public enum WaveeLogLevel : byte { Trace, Debug, Info, Warning, Error, Critical }

/// <summary>One structured log record (kept in the ring buffer +, for Error+, the crash log).</summary>
public readonly record struct WaveeLogEntry(long UnixMs, WaveeLogLevel Level, string Category, string Message, string? Exception)
{
    public string Format()
    {
        char l = Level switch
        {
            WaveeLogLevel.Trace => 'T', WaveeLogLevel.Debug => 'D', WaveeLogLevel.Info => 'I',
            WaveeLogLevel.Warning => 'W', WaveeLogLevel.Error => 'E', _ => 'C',
        };
        return Exception is null ? $"{l} [{Category}] {Message}" : $"{l} [{Category}] {Message} | {Exception}";
    }
}

public interface IWaveeLog
{
    void Log(WaveeLogLevel level, string category, string message, Exception? ex = null);
}

public static class WaveeLogExtensions
{
    public static void Trace(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Trace, cat, msg);
    public static void Debug(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Debug, cat, msg);
    public static void Info(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Info, cat, msg);
    public static void Warn(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Warning, cat, msg, ex);
    public static void Error(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Error, cat, msg, ex);
    public static void Critical(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Critical, cat, msg, ex);
}

/// <summary>A PII wrapper whose <see cref="ToString"/> never reveals the value (tokens, credentials). Call
/// <see cref="Reveal"/> only at the point of use; logging it prints <c>***</c>.</summary>
public readonly struct Secret(string value)
{
    readonly string _value = value;
    public string Reveal() => _value;
    public override string ToString() => "***";
}
