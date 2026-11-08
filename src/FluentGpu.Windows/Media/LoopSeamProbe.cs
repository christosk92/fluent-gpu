using System;
using System.Diagnostics;
using System.Globalization;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The loop seam's measurement: how a looping source behaves where its end meets its start, one always-on line per seam for the
/// first <see cref="MaxLogged"/> seams of a source (<c>[video] loop.seam</c>).
/// <para><b>How.</b> From <see cref="LeadSeconds"/> before a seam to <see cref="TailSeconds"/> after it the engine refreshes every
/// <see cref="NearMs"/> ms (a bounded window: the rest of the loop keeps the normal cadence), and each refresh hands this probe its time,
/// the playhead and the renderer's frame count. The line names where the playhead was when it crossed (<c>wrapAt</c>: for the engine's own
/// loop, how far short of the clip's end it jumped back, the tail of the clip that was never shown), the longest time the playhead stood
/// still around the seam (<c>clockStallMs</c>), and for the loop that just ended its wall time and the frames the renderer presented in it
/// (<c>loopMs</c>, <c>loopFrames</c>; the engine publishes FRAMES_RENDERED in batches of about half a second, so one loop's count is good to
/// a batch, a run of loops' sum to far better).</para>
/// <para><b>Which seam.</b> <see cref="Period"/> 0 is the Media Engine's own loop: the playhead jumps back from the end to 0. A
/// positive period is a gapless loop (<see cref="Mp4LoopRemux"/>): the playhead runs on, and a seam is every multiple of the clip's length.</para>
/// <para>Engine thread only. Allocates only the line it returns (at most <see cref="MaxLogged"/> per source).</para>
/// </summary>
internal sealed class LoopSeamProbe
{
    /// <summary>Seams measured (and logged) per source.</summary>
    public const int MaxLogged = 6;

    /// <summary>The engine's refresh cadence inside a seam's window.</summary>
    public const int NearMs = 1;

    /// <summary>The window around a seam: how long before it the sampling starts and how long after it it closes. The lead covers the
    /// engine's own loop jumping back up to half a second early.</summary>
    private const double LeadSeconds = 0.60, TailSeconds = 0.40;

    /// <summary>Seconds between seams for a gapless loop; 0 for the Media Engine's own loop.</summary>
    public double Period { get; private set; }

    private bool _open, _crossed;
    private long _openTicks, _stillTicks;
    private double _last, _before, _stall;
    private long _seamTicks, _seamRendered;     // this window's crossing
    private long _prevTicks, _prevRendered;      // the previous window's crossing (0: none yet)
    private int _seams;

    /// <summary>A new source (or loop setting): forget every sample.</summary>
    public void Reset(double period)
    {
        Period = period;
        _open = _crossed = false;
        _openTicks = _stillTicks = _seamTicks = _seamRendered = _prevTicks = _prevRendered = 0;
        _last = _before = _stall = 0;
        _seams = 0;
    }

    private double Per(double duration) => Period > 0 ? Period : duration;

    /// <summary>Should the engine refresh at <see cref="NearMs"/>: a seam is near, or its window is open.</summary>
    public bool Near(double position, double duration)
    {
        if (_open) return true;
        double per = Per(duration);
        return _seams < MaxLogged && per > 0 && per - (position % per) < LeadSeconds;
    }

    /// <summary>One sample: the refresh's time, the playhead and the frames presented so far (accumulated across the engine's resets).
    /// Returns the seam's line when its window closes.</summary>
    public string? Sample(long ticks, double position, double duration, long rendered, int engineId)
    {
        double per = Per(duration);
        if (!(per > 0) || _seams >= MaxLogged) return null;
        double since = position % per;
        // A crossing counts only between two samples of one window (the previous window's last sample is a seam ago).
        // A gapless loop's seams are its clip boundaries, and its last one is the engine's own loop (the backstop) jumping back.
        bool crossed = _open && (position < _last - per * 0.5 || (Period > 0 && Math.Floor(position / per) > Math.Floor(_last / per)));
        if (!_open)
        {
            if (per - since >= LeadSeconds) return null;
            _open = true; _crossed = false; _openTicks = ticks; _stillTicks = ticks; _stall = 0;
        }
        else if (position != _last) _stillTicks = ticks;
        else _stall = Math.Max(_stall, (ticks - _stillTicks) * 1000.0 / Stopwatch.Frequency);
        if (crossed && !_crossed) { _crossed = true; _before = _last; _seamTicks = ticks; _seamRendered = rendered; }
        _last = position;
        bool settled = _crossed && since >= TailSeconds && since < per - LeadSeconds;
        if (!settled && ticks - _openTicks < Stopwatch.Frequency * 3) return null;   // a paused or stalled window never pins the cadence
        _open = false;
        if (!_crossed) return null;
        _seams++;
        string loop = _prevTicks == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $" loopMs={(_seamTicks - _prevTicks) * 1000.0 / Stopwatch.Frequency:0} loopFrames={_seamRendered - _prevRendered}");
        _prevTicks = _seamTicks; _prevRendered = _seamRendered;
        return string.Create(CultureInfo.InvariantCulture,
            $"[video] loop.seam engine={engineId} n={_seams} kind={(Period > 0 ? "gapless" : "native")} clipS={per:0.000} wrapAt={_before:0.000} clockStallMs={_stall:0}{loop}");
    }
}
