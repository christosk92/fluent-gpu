namespace FluentGpu.Hosting;

/// <summary>
/// When a reactive unit (one computation: a component render, an effect, a binding — with its synchronous memo pulls and
/// reconciliation) runs longer than a frame period, the hosted flush does NOT slice around it: every flush runs to
/// quiescence (reconciler-hooks.md §0bis). The unit is the finding. This decides when the host writes its always-on
/// <c>[signals.slow-unit]</c> line: immediately for the first slow unit, then at most once per second, carrying the
/// count of slow units the rate limit folded in — a log cadence, never a work budget. Pure and clock-free (the caller
/// passes Stopwatch ticks), so its rules are unit-tested directly.
/// </summary>
public sealed class SlowReactiveUnits
{
    private long _lastReportTicks = long.MinValue;
    private int _folded;

    /// <summary>Slow units seen so far (always-on counter).</summary>
    public int Total { get; private set; }

    /// <summary>Is a unit of <paramref name="unitTicks"/> slow against a frame of <paramref name="periodTicks"/>?</summary>
    public static bool IsSlow(long unitTicks, long periodTicks) => periodTicks > 0 && unitTicks > periodTicks;

    /// <summary>Note a flush's longest unit. True when the host should write the report line now;
    /// <paramref name="folded"/> = slow units since the previous line that this one stands for.</summary>
    public bool Note(long unitTicks, long periodTicks, long nowTicks, long ticksPerSecond, out int folded)
    {
        folded = 0;
        if (!IsSlow(unitTicks, periodTicks)) return false;
        Total++;
        if (_lastReportTicks != long.MinValue && nowTicks - _lastReportTicks < ticksPerSecond)
        {
            _folded++;
            return false;
        }
        folded = _folded;
        _folded = 0;
        _lastReportTicks = nowTicks;
        return true;
    }
}
