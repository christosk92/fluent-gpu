using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>UseThrottledValue's trailing sample closed the window without starting a new one, so under a continuous
/// source the next change emitted as a fresh leading edge right behind it: two emits per window (in the same frame when
/// the source changes every frame), twice the documented "at most once per ms" rate.</summary>
public sealed class ThrottleCooldownTests
{
    private const float Ms = 100f;

    private static (ThrottleCell<int> Cell, Signal<int> Source, System.Action<double> SetNow, HostTimerQueue Queue) Make()
    {
        double now = 0;
        var q = new HostTimerQueue(() => now);
        var source = new Signal<int>(0);
        var cell = new ThrottleCell<int> { Queue = q, Source = source, Ms = Ms, Output = new Signal<int>(0), Latest = 0 };
        return (cell, source, t => now = t, q);
    }

    // One frame at time t: timers drain at frame top, then the source change runs the watcher (OnChange).
    private static void Frame(ThrottleCell<int> cell, Signal<int> source, System.Action<double> setNow, HostTimerQueue q,
        double t, int? value, List<double> emits)
    {
        setNow(t);
        int before = cell.Output.Peek();
        q.Drain();
        if (cell.Output.Peek() != before) { emits.Add(t); before = cell.Output.Peek(); }
        if (value is int v) { source.Value = v; cell.OnChange(); }
        if (cell.Output.Peek() != before) emits.Add(t);
    }

    [Fact]
    public void AContinuousSource_EmitsAtMostOncePerWindow()
    {
        var (cell, source, setNow, q) = Make();
        var emits = new List<double>();
        int v = 0;
        for (double t = 0; t <= 400; t += 10) Frame(cell, source, setNow, q, t, ++v, emits);

        Assert.True(emits.Count >= 4, $"expected leading + trailing emits, got {emits.Count}");
        for (int i = 1; i < emits.Count; i++)
            Assert.True(emits[i] - emits[i - 1] >= Ms, $"emits {emits[i - 1]} and {emits[i]} ms are inside one {Ms} ms window: [{string.Join(", ", emits)}]");
    }

    [Fact]
    public void AfterTheSourceGoesQuiet_TheLastValueLands_AndTheNextChangeEmitsImmediately()
    {
        var (cell, source, setNow, q) = Make();
        var emits = new List<double>();
        int v = 0;
        for (double t = 0; t <= 400; t += 10) Frame(cell, source, setNow, q, t, ++v, emits);
        for (double t = 410; t <= 700; t += 10) Frame(cell, source, setNow, q, t, null, emits);
        Assert.Equal(v, cell.Output.Peek());     // the final value is not lost

        Frame(cell, source, setNow, q, 710, ++v, emits);
        Assert.Equal(v, cell.Output.Peek());     // a quiet window reopened the leading edge
        Assert.Equal(710, emits[^1]);
    }
}
