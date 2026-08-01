using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WinUIBench;

internal static class BenchClock
{
    internal static long ProcessStartQpc { get; private set; }

    [ModuleInitializer]
    internal static void Initialize() => ProcessStartQpc = Stopwatch.GetTimestamp();
}
