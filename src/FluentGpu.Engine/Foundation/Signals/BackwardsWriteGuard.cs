using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Signals;

/// <summary>
/// A DEBUG-only convergence-risk tripwire (research adjustment #11; modeled on <see cref="FluentGpu.Hooks.ReuseGuard"/>
/// and <see cref="Diag"/>). When a tracked computation — an auto-tracked <c>UseEffect</c> body or a bind thunk — WRITES
/// a signal it also READS in the same run, that write re-marks the running computation stale, so it schedules itself
/// again: a convergence risk (the pattern Compose calls a "backwards write" and Angular guards with
/// <c>allowSignalWrites</c>). This surfaces exactly that, once per computation (throttled), naming the signal + the
/// computation.
/// <para>
/// Cost discipline (matches <c>Diag</c> / <c>RenderBudget</c> / <see cref="FluentGpu.Hooks.ReuseGuard"/>): the whole
/// facility is gated by the const <see cref="CompiledIn"/> (<c>false</c> unless <c>DEBUG</c>/<c>FLUENTGPU_DIAG</c>), so
/// the <c>if (BackwardsWriteGuard.CompiledIn &amp;&amp; BackwardsWriteGuard.Enabled) { … }</c> guard on the signal write
/// path is dead-code-eliminated in the shipping AOT binary — zero bytes, zero probe cost. When compiled in it defaults
/// ON (a kill-switch: <c>FG_BACKWARDS_WRITE=0</c> turns it off) and is report-only unless <c>FG_BACKWARDS_WRITE_THROW=1</c>.
/// On the non-violating path it allocates nothing (a subscriber-list <c>Contains</c> + a reference compare), so the
/// zero-alloc hot window is preserved even with the guard live.
/// </para>
/// </summary>
public static class BackwardsWriteGuard
{
    /// <summary>Compile-time master switch — <c>false</c> in release so the guard on the signal write path folds away.</summary>
    public const bool CompiledIn =
#if DEBUG || FLUENTGPU_DIAG
        true;
#else
        false;
#endif

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): defaults ON, kill-switch
    /// <c>FG_BACKWARDS_WRITE=0</c> disables it.</summary>
    public static bool Enabled = CompiledIn && !Diag.EnvFlagDisabled("FG_BACKWARDS_WRITE");

    /// <summary>When set, a detected read+write THROWS <see cref="BackwardsWriteException"/> instead of only reporting —
    /// <c>FG_BACKWARDS_WRITE_THROW=1</c>, or a gate scoping the strict path. Default report-only.</summary>
    public static bool ThrowOnViolation = CompiledIn && Diag.EnvFlag("FG_BACKWARDS_WRITE_THROW");

    /// <summary>Count of violations since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    // Throttle: suppress consecutive repeats of the SAME running computation (a self-retriggering effect would
    // otherwise report once per flush iteration). Reset between gate scenarios.
    private static Computation? _lastReported;

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; _lastReported = null; }

    // Called from Signal<T>.SetIfChanged. `current` is the running computation (null ⇒ not mid-run ⇒ nothing to flag);
    // `subs` is the signal's subscriber list — if it already contains `current`, that computation READ this signal
    // earlier THIS run (RunComputation cleared the links at run start), so this write is a read+write. `valueType` is
    // used only to build the (rare) violation message, so it never allocates on the passing path.
    internal static void CheckWrite(Computation? current, List<Computation> subs, Type valueType)
    {
        if (current is null || ReferenceEquals(current, _lastReported)) return;
        if (!subs.Contains(current)) return;
        Report(current, "Signal<" + valueType.Name + ">");
    }

    /// <summary>The <see cref="FloatSignal"/> variant — no generic type to name, so it passes a literal description.</summary>
    internal static void CheckWriteFloat(Computation? current, List<Computation> subs)
    {
        if (current is null || ReferenceEquals(current, _lastReported)) return;
        if (!subs.Contains(current)) return;
        Report(current, "FloatSignal");
    }

    private static void Report(Computation current, string signalDesc)
    {
        _lastReported = current;
        Violations++;
        string msg = $"[backwards-write] {current.GetType().Name} reads and writes the same signal ({signalDesc}) in one run "
                   + "— convergence risk (the write re-marks the running computation stale, scheduling it again). "
                   + "Derive the value instead, or split the read and the write across separate effects.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new BackwardsWriteException(msg);
    }
}

/// <summary>Thrown by <see cref="BackwardsWriteGuard"/> in strict mode (<c>FG_BACKWARDS_WRITE_THROW</c>) when a tracked
/// computation wrote a signal it also read in the same run. Never thrown in release (the guard is compiled out).</summary>
public sealed class BackwardsWriteException(string message) : InvalidOperationException(message);
