using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FluentGpuBench;

/// <summary>
/// Process-entry anchor for cold-load scenarios. This must be a <see cref="ModuleInitializerAttribute"/> and not a
/// static field on a class the harness touches later: a <c>beforefieldinit</c> static field is first initialized at its
/// first use, which for the harness is <em>after</em> the whole engine is already up — silently excluding bring-up from
/// the measurement. Mirrors <c>WinUIBench.BenchClock</c> exactly so both hosts anchor at the same point.
/// </summary>
internal static class BenchClock
{
    internal static long ProcessStartQpc { get; private set; }

    [ModuleInitializer]
    internal static void Initialize() => ProcessStartQpc = Stopwatch.GetTimestamp();
}
