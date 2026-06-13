using System;
using System.IO;

namespace FluentGpu.Foundation;

/// <summary>TEMPORARY opt-in scroll/input trace (set the env var <c>FG_SCROLL_LOG=1</c> before launching the gallery).
/// A diagnostic for tuning REAL-hardware touchpad/touch scroll feel — the headless harness can prove the physics math
/// but cannot see what a given precision touchpad actually emits or how the gesture feels. Writes one line per raw input
/// event (wheel notch / hi-res classification / touch down·move·up) and per scroll step (1:1 pan, eased target, overscroll
/// band, fling seed, spring-back) to <c>%TEMP%\fg-scroll.log</c> (and the console if one is attached).
///
/// Gated by a static bool read once at startup: when <see cref="On"/> is false (the default, and every headless gate run)
/// nothing is written. Hot-path call sites MUST guard with <c>if (ScrollLog.On)</c> so the interpolated message string is
/// never even built when disabled — keeping the zero-alloc frame phases clean. Remove once the feel is dialed in.</summary>
public static class ScrollLog
{
    /// <summary>True iff <c>FG_SCROLL_LOG=1</c> was set in the environment at process start.</summary>
    public static readonly bool On = Environment.GetEnvironmentVariable("FG_SCROLL_LOG") == "1";

    private static readonly object _gate = new();
    private static StreamWriter? _file;
    private static long _t0;
    private static bool _opened;

    /// <summary>Write one trace line (prefixed with ms since the first line). No-op when <see cref="On"/> is false.</summary>
    public static void Line(string msg)
    {
        if (!On) return;
        lock (_gate)
        {
            if (!_opened) { _opened = true; _t0 = Environment.TickCount64; _file = TryOpen(); }
            long ms = Environment.TickCount64 - _t0;
            string line = ms.ToString().PadLeft(7) + "  " + msg;
            try { _file?.WriteLine(line); } catch { /* best-effort */ }
            try { Console.WriteLine("[scroll] " + line); } catch { }
        }
    }

    private static StreamWriter? TryOpen()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "fg-scroll.log");
            var w = new StreamWriter(path, append: false) { AutoFlush = true };
            w.WriteLine("# fg-scroll trace — touchpad/touch scroll diagnostic");
            try { Console.WriteLine("[scroll] writing to " + path); } catch { }
            return w;
        }
        catch { return null; }
    }
}
